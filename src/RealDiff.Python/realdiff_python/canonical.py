from __future__ import annotations

import hashlib
import json
import math
import os
import re
import struct
from dataclasses import dataclass
from types import MappingProxyType


MISSING = object()
DEFAULT_SENSITIVE_NAMES = ("password", "token", "secret", "key", "ssn", "email", "auth", "credential")
CREDENTIAL_PATTERN = re.compile(
    r"(?:eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}|(?:AKIA|ASIA)[A-Z0-9]{16}|-----BEGIN (?:RSA |EC |OPENSSH )?(?:PRIVATE KEY|CERTIFICATE)-----|[A-Za-z0-9+/]{40,}={0,2})"
)


@dataclass
class DigestStatistics:
    values_digested: int = 0
    depth_limited: int = 0
    blocklisted: int = 0
    errored: int = 0
    rendered_truncated: int = 0
    unreadable_fields: int = 0
    ambiguous_map_entries: int = 0


@dataclass(frozen=True)
class CanonicalValue:
    digest: str
    rendered: str
    partial: bool


@dataclass(frozen=True)
class RedactionPolicy:
    names: tuple[str, ...]
    types: tuple[str, ...]
    paths: tuple[str, ...]

    @classmethod
    def from_environment(cls) -> "RedactionPolicy":
        names = DEFAULT_SENSITIVE_NAMES + _read_list(os.environ.get("REALDIFF_REDACT_NAMES", ""))
        return cls(names, _read_list(os.environ.get("REALDIFF_REDACT_TYPES", "")), _read_list(os.environ.get("REALDIFF_REDACT_PATHS", "")))

    def sensitive_name(self, name: str | None) -> bool:
        lowered = (name or "").lower()
        return bool(lowered) and any(pattern.lower() in lowered for pattern in self.names)

    def sensitive_type(self, value: object) -> bool:
        return _type_name(type(value)) in self.types

    def sensitive_path(self, path: str | None) -> bool:
        normalized = (path or "").replace("\\", "/").strip("/")
        return any(normalized == prefix or normalized.startswith(prefix.rstrip("/") + "/") for prefix in self.paths)


class Canonicalizer:
    def __init__(
        self,
        statistics: DigestStatistics | None = None,
        redaction: RedactionPolicy | None = None,
        max_depth: int = 8,
        max_items: int = 100,
        render_limit: int = 4096,
    ):
        self.statistics = statistics or DigestStatistics()
        self.redaction = redaction or RedactionPolicy.from_environment()
        self.max_depth = max_depth
        self.max_items = max_items
        self.render_limit = render_limit

    def digest(self, value: object, name: str | None = None, source_path: str | None = None) -> CanonicalValue:
        full = _Encoder(self, count=True, redact=False, source_path=source_path).encode(value, name, 0)
        rendered = _Encoder(self, count=False, redact=True, source_path=source_path).encode(value, name, 0)
        if len(rendered) > self.render_limit:
            rendered = rendered[: self.render_limit] + "<truncated>"
            self.statistics.rendered_truncated += 1
        digest = hashlib.sha256(full.encode("utf-8")).hexdigest()
        return CanonicalValue(f"sha256:{digest}", rendered, _is_partial(rendered) or _is_partial(full))


class _Encoder:
    def __init__(self, owner: Canonicalizer, count: bool, redact: bool, source_path: str | None):
        self.owner = owner
        self.count = count
        self.redact = redact
        self.source_path = source_path
        self.references: dict[int, int] = {}

    def encode(self, value: object, name: str | None, depth: int) -> str:
        if self.count:
            self.owner.statistics.values_digested += 1
        if self.redact and (
            self.owner.redaction.sensitive_name(name)
            or self.owner.redaction.sensitive_type(value)
            or self.owner.redaction.sensitive_path(self.source_path)
        ):
            return "<redacted>"
        if depth > self.owner.max_depth:
            if self.count:
                self.owner.statistics.depth_limited += 1
            return f"<depth:{_type_name(type(value))}>"
        if value is MISSING:
            return "missing"
        if value is None:
            return "none"
        if type(value) is bool:
            return "bool:true" if value else "bool:false"
        if type(value) is int:
            return f"int:{value}"
        if type(value) is float:
            return _float_text(value)
        if type(value) is str:
            if self.redact and CREDENTIAL_PATTERN.search(value):
                return "<redacted>"
            return "str:" + json.dumps(value, ensure_ascii=False, separators=(",", ":"))
        if type(value) is bytes:
            return "bytes:" + value.hex()
        if type(value) is bytearray:
            return "bytearray:" + bytes(value).hex()
        if type(value) in (list, tuple):
            return self._sequence(value, depth)
        if type(value) in (set, frozenset):
            return self._set(value, depth)
        if type(value) is dict:
            return self._dict(value, depth)
        return self._object(value, depth)

    def _reference(self, value: object) -> tuple[int, bool]:
        identity = id(value)
        reference = self.references.get(identity)
        if reference is not None:
            return reference, True
        reference = len(self.references) + 1
        self.references[identity] = reference
        return reference, False

    def _sequence(self, value: list | tuple, depth: int) -> str:
        reference, seen = self._reference(value)
        if seen:
            return f"ref:{reference}"
        items = list.__getitem__(value, slice(0, self.owner.max_items)) if type(value) is list else tuple.__getitem__(value, slice(0, self.owner.max_items))
        encoded = [self.encode(item, None, depth + 1) for item in items]
        if len(value) > self.owner.max_items:
            encoded.append(self._skipped("CollectionLimit"))
        return f"{type(value).__name__}#{reference}[{','.join(encoded)}]"

    def _set(self, value: set | frozenset, depth: int) -> str:
        reference, seen = self._reference(value)
        if seen:
            return f"ref:{reference}"
        values = set.__iter__(value) if type(value) is set else frozenset.__iter__(value)
        probes = sorted(_Encoder(self.owner, False, False, self.source_path).encode(item, None, depth + 1) for item in values)
        if len(probes) > self.owner.max_items:
            probes = probes[: self.owner.max_items] + [self._skipped("CollectionLimit")]
        return f"{type(value).__name__}#{reference}[{','.join(probes)}]"

    def _dict(self, value: dict, depth: int) -> str:
        reference, seen = self._reference(value)
        if seen:
            return f"ref:{reference}"
        entries: list[tuple[str, object, object]] = []
        iterator = dict.items(value)
        for key, item in iterator:
            probe = _Encoder(self.owner, False, False, self.source_path).encode(key, None, depth + 1)
            entries.append((probe, key, item))
        entries.sort(key=lambda entry: entry[0])
        encoded: list[str] = []
        for index, (probe, key, item) in enumerate(entries[: self.owner.max_items]):
            if index > 0 and entries[index - 1][0] == probe:
                if self.count:
                    self.owner.statistics.ambiguous_map_entries += 1
                encoded.append(self._skipped("AmbiguousMapEntry"))
                continue
            key_text = self.encode(key, None, depth + 1)
            field_name = key if type(key) is str else None
            encoded.append(f"{key_text}=>{self.encode(item, field_name, depth + 1)}")
        if len(entries) > self.owner.max_items:
            encoded.append(self._skipped("CollectionLimit"))
        return f"dict#{reference}{{{','.join(encoded)}}}"

    def _object(self, value: object, depth: int) -> str:
        reference, seen = self._reference(value)
        if seen:
            return f"ref:{reference}"
        type_ = type(value)
        type_name = _type_name(type_)
        hazards = _hazards(type_)
        if _is_container_subclass(type_):
            hazards.append("ContainerSubclass")
        try:
            fields = object.__getattribute__(value, "__dict__")
        except BaseException as error:
            if self.count:
                self.owner.statistics.errored += 1
                self.owner.statistics.unreadable_fields += 1
            return f"object:{type_name}#{reference}{{<error:Python:{type(error).__name__}>}}"
        if type(fields) is not dict:
            hazards.append("NoConcreteDict")
            fields = {}
        encoded = [
            f"{json.dumps(key, ensure_ascii=False)}={self.encode(item, key, depth + 1)}"
            for key, item in sorted(dict.items(fields), key=lambda entry: entry[0])
            if type(key) is str
        ]
        encoded.extend(self._skipped(hazard) for hazard in sorted(set(hazards)))
        return f"object:{type_name}#{reference}{{{','.join(encoded)}}}"

    def _skipped(self, detail: str) -> str:
        if self.count:
            self.owner.statistics.blocklisted += 1
            self.owner.statistics.unreadable_fields += 1
        return f"<skipped:Python:{detail}>"


def _hazards(type_: type) -> list[str]:
    hazards: list[str] = []
    try:
        hierarchy = type.__getattribute__(type_, "__mro__")
    except BaseException:
        return ["UnreadableType"]
    for owner in hierarchy:
        namespace = type.__getattribute__(owner, "__dict__")
        if "__slots__" in namespace:
            hazards.append("Slots")
        if "__getattr__" in namespace:
            hazards.append("GetAttrOverride")
        if "__getattribute__" in namespace and owner is not object:
            hazards.append("GetAttributeOverride")
        if any(type(value) is property for value in MappingProxyType.values(namespace)):
            hazards.append("PropertyDescriptor")
    return hazards


def _is_container_subclass(type_: type) -> bool:
    hierarchy = type.__getattribute__(type_, "__mro__")
    return any(base in (list, tuple, dict, set, frozenset) for base in hierarchy[1:])


def _float_text(value: float) -> str:
    if math.isnan(value):
        return "float:nan"
    if value == 0.0 and math.copysign(1.0, value) < 0:
        return "float:-0.0"
    return "float:0x" + struct.pack(">d", value).hex()


def _type_name(type_: type) -> str:
    return f"{type_.__module__}.{type_.__qualname__}"


def _read_list(value: str) -> tuple[str, ...]:
    return tuple(item.strip() for item in value.replace(";", ",").split(",") if item.strip())


def _is_partial(value: str) -> bool:
    return any(marker in value for marker in ("<skipped:", "<depth:", "<error:", "<truncated>"))