from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from pathlib import Path
from types import CodeType, FrameType
from typing import Callable


MINIMUM_VERSION = (3, 12)
TOOL_ID = 5


def _path_parts(value: str) -> tuple[str, ...]:
    return tuple(part for part in value.replace("\\", "/").strip("/").split("/") if part)


def _prefix_matches(path: tuple[str, ...], prefix: tuple[str, ...]) -> bool:
    return len(prefix) <= len(path) and path[: len(prefix)] == prefix


@dataclass(frozen=True)
class Scope:
    root: Path
    includes: tuple[tuple[str, ...], ...]
    excludes: tuple[tuple[str, ...], ...]

    @classmethod
    def from_environment(cls) -> "Scope":
        root_value = os.environ.get("REALDIFF_REPOSITORY_ROOT")
        if not root_value:
            raise RuntimeError("REALDIFF_REPOSITORY_ROOT is required for Python tracing")
        includes = _read_list(os.environ.get("REALDIFF_INCLUDE_NAMESPACES", ""))
        excludes = _read_list(os.environ.get("REALDIFF_EXCLUDE_NAMESPACES", ""))
        return cls(Path(root_value).resolve(), includes, excludes)

    def relative_path(self, code: CodeType) -> str | None:
        relative = self.repository_path(code)
        if relative is None:
            return None
        if self.is_excluded(relative):
            return None
        if not self.is_included(relative):
            return None
        return relative

    def is_excluded(self, relative: str) -> bool:
        parts = _path_parts(relative)
        return any(_prefix_matches(parts, prefix) for prefix in self.excludes)

    def is_included(self, relative: str) -> bool:
        if not self.includes:
            return True
        parts = _path_parts(relative)
        return any(_prefix_matches(parts, prefix) for prefix in self.includes)

    def repository_path(self, code: CodeType) -> str | None:
        path, _, resolution = self.source_location(code)
        return path if resolution == "debugInfo" else None

    def source_location(self, code: CodeType) -> tuple[str | None, int, str]:
        filename = code.co_filename
        if not filename or filename.startswith("<"):
            return None, 0, "debugInfoMissing"
        try:
            relative = Path(filename).resolve().relative_to(self.root).as_posix()
        except (OSError, ValueError):
            return None, 0, "unresolved"
        return relative, max(1, code.co_firstlineno), "debugInfo"


def _read_list(value: str) -> tuple[tuple[str, ...], ...]:
    return tuple(
        _path_parts(item)
        for item in value.replace(";", ",").split(",")
        if item.strip()
    )


class Monitor:
    def __init__(
        self,
        scope: Scope,
        observer: Callable[[str, CodeType, object | None, FrameType], None] | None = None,
    ):
        self.scope = scope
        self.observer = observer
        self._scoped_codes: dict[CodeType, bool] = {}

    def start(self, code: CodeType, instruction_offset: int) -> None:
        del instruction_offset
        if not self._is_scoped(code):
            return
        self._observe("start", code, None)

    def resume(self, code: CodeType, instruction_offset: int) -> None:
        del instruction_offset
        if not self._is_scoped(code):
            return
        self._observe("resume", code, None)

    def returned(self, code: CodeType, instruction_offset: int, value: object) -> None:
        del instruction_offset
        if not self._is_scoped(code):
            return
        self._observe("return", code, value)

    def yielded(self, code: CodeType, instruction_offset: int, value: object) -> None:
        del instruction_offset
        if not self._is_scoped(code):
            return
        self._observe("yield", code, value)

    def raised(self, code: CodeType, instruction_offset: int, exception: BaseException) -> None:
        del instruction_offset
        if not self._is_scoped(code):
            return
        self._observe("raise", code, exception)

    def unwind(self, code: CodeType, instruction_offset: int, exception: BaseException) -> None:
        del instruction_offset
        if not self._is_scoped(code):
            return
        self._observe("unwind", code, exception)

    def _is_scoped(self, code: CodeType) -> bool:
        scoped = self._scoped_codes.get(code)
        if scoped is None:
            scoped = self.scope.relative_path(code) is not None
            self._scoped_codes[code] = scoped
        return scoped

    def _observe(self, event: str, code: CodeType, value: object | None) -> None:
        if self.observer is not None:
            self.observer(event, code, value, sys._getframe(2))


_monitor: Monitor | None = None


def install(
    observer: Callable[[str, CodeType, object | None, FrameType], None] | None = None,
    scope: Scope | None = None,
) -> Monitor:
    global _monitor
    if sys.version_info < MINIMUM_VERSION or not hasattr(sys, "monitoring"):
        raise RuntimeError("RealDiff Python tracing requires Python 3.12+ with sys.monitoring")
    if _monitor is not None:
        return _monitor
    if sys.monitoring.get_tool(TOOL_ID) is not None:
        raise RuntimeError(f"sys.monitoring tool id {TOOL_ID} is already in use")

    monitor = Monitor(scope or Scope.from_environment(), observer)
    events = sys.monitoring.events
    sys.monitoring.use_tool_id(TOOL_ID, "realdiff")
    sys.monitoring.register_callback(TOOL_ID, events.PY_START, monitor.start)
    sys.monitoring.register_callback(TOOL_ID, events.PY_RESUME, monitor.resume)
    sys.monitoring.register_callback(TOOL_ID, events.PY_RETURN, monitor.returned)
    sys.monitoring.register_callback(TOOL_ID, events.PY_YIELD, monitor.yielded)
    sys.monitoring.register_callback(TOOL_ID, events.RAISE, monitor.raised)
    sys.monitoring.register_callback(TOOL_ID, events.PY_UNWIND, monitor.unwind)
    sys.monitoring.set_events(
        TOOL_ID,
        events.PY_START
        | events.PY_RESUME
        | events.PY_RETURN
        | events.PY_YIELD
        | events.RAISE
        | events.PY_UNWIND,
    )
    _monitor = monitor
    return monitor


def uninstall() -> None:
    global _monitor
    if _monitor is None:
        return
    sys.monitoring.set_events(TOOL_ID, sys.monitoring.events.NO_EVENTS)
    sys.monitoring.free_tool_id(TOOL_ID)
    _monitor = None