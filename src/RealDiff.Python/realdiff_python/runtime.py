from __future__ import annotations

import atexit
import json
import os
import threading
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from types import CodeType, FrameType

from .canonical import Canonicalizer, DigestStatistics, MISSING
from .monitor import Scope


@dataclass
class CallState:
    call_id: int
    parent_call_id: int | None
    depth: int
    ordinal: int
    method: str
    file_path: str
    line: int
    thread_id: int
    args_digest: str
    args_rendered: str
    test_id: str = "(no-test)"
    pending_exception: BaseException | None = None
    suspended: bool = False


@dataclass(frozen=True)
class Member:
    module: str
    method: str
    file_path: str
    line: int
    status: str
    return_kind: str
    skip_reason: str | None = None
    detail: str | None = None


class Runtime:
    def __init__(self, trace_path: Path, scope: Scope):
        self.trace_path = trace_path
        self.manifest_path = trace_path.with_name(trace_path.stem + ".manifest.ndjson")
        self.scope = scope
        self._lock = threading.RLock()
        self._next_call_id = 1
        self._states: dict[int, CallState] = {}
        self._stacks: dict[int, list[int]] = defaultdict(list)
        self._ordinals: dict[tuple[str, str], int] = defaultdict(int)
        self._members: dict[str, Member] = {}
        self._module_calls: dict[str, int] = defaultdict(int)
        self._digest_statistics = DigestStatistics()
        self._canonicalizer = Canonicalizer(statistics=self._digest_statistics)
        self._written = 0
        self._closed = False
        trace_path.parent.mkdir(parents=True, exist_ok=True)
        self._stream = trace_path.open("w", encoding="utf-8", newline="\n")
        atexit.register(self.close)

    @classmethod
    def from_environment(cls) -> "Runtime":
        trace = os.environ.get("REALDIFF_TRACE")
        if not trace:
            raise RuntimeError("REALDIFF_TRACE is required for Python tracing")
        return cls(Path(trace).resolve(), Scope.from_environment())

    def on_event(self, event: str, code: CodeType, value: object | None, frame: FrameType) -> None:
        with self._lock:
            if event == "start":
                self._start(code, frame)
            elif event == "resume":
                self._resume(frame)
            elif event == "yield":
                self._suspend(frame)
            elif event == "raise":
                self._raised(frame, value)
            elif event == "return":
                self._complete(frame, return_value=value)
            elif event == "unwind":
                self._complete(frame, exception=value)

    def _start(self, code: CodeType, frame: FrameType) -> None:
        file_path = self.scope.relative_path(code)
        if file_path is None:
            return
        module = _module_name(file_path)
        method = _method_name(file_path, code)
        if code.co_name == "<module>":
            self._members.setdefault(
                method,
                Member(module, method, file_path, code.co_firstlineno, "Skipped", "Module", "UnsupportedShape", "Python: ModuleBody"),
            )
            return

        thread_id = threading.get_native_id()
        stack = self._stacks[thread_id]
        call_id = self._next_call_id
        self._next_call_id += 1
        test_id = "(no-test)"
        ordinal_key = (test_id, method)
        ordinal = self._ordinals[ordinal_key]
        self._ordinals[ordinal_key] += 1
        state = CallState(
            call_id=call_id,
            parent_call_id=stack[-1] if stack else None,
            depth=len(stack),
            ordinal=ordinal,
            method=method,
            file_path=file_path,
            line=max(1, code.co_firstlineno),
            thread_id=thread_id,
            args_digest="",
            args_rendered="",
        )
        arguments = _capture_arguments(code, frame)
        canonical_arguments = self._canonicalizer.digest(arguments, source_path=file_path)
        state.args_digest = canonical_arguments.digest
        state.args_rendered = canonical_arguments.rendered
        self._states[id(frame)] = state
        stack.append(call_id)
        self._members.setdefault(
            method,
            Member(module, method, file_path, state.line, "Patched", _return_kind(code)),
        )

    def _resume(self, frame: FrameType) -> None:
        state = self._states.get(id(frame))
        if state is None or not state.suspended:
            return
        stack = self._stacks[state.thread_id]
        state.parent_call_id = stack[-1] if stack else None
        state.depth = len(stack)
        stack.append(state.call_id)
        state.suspended = False

    def _suspend(self, frame: FrameType) -> None:
        state = self._states.get(id(frame))
        if state is None:
            return
        self._pop_stack(state)
        state.suspended = True

    def _raised(self, frame: FrameType, value: object | None) -> None:
        state = self._states.get(id(frame))
        if state is not None and value is not None:
            state.pending_exception = value

    def _complete(
        self,
        frame: FrameType,
        return_value: object | None = None,
        exception: object | None = None,
    ) -> None:
        state = self._states.pop(id(frame), None)
        if state is None:
            return
        self._pop_stack(state)
        record: dict[str, object] = {
            "testId": state.test_id,
            "methodFullName": state.method,
            "filePath": state.file_path,
            "filePathResolution": "debugInfo",
            "line": state.line,
            "callDepth": state.depth,
            "callId": state.call_id,
            "ordinal": state.ordinal,
            "argsDigest": state.args_digest,
            "argsRendered": state.args_rendered,
            "threadId": state.thread_id,
        }
        if state.parent_call_id is not None:
            record["parentCallId"] = state.parent_call_id
        if exception is not None:
            record["exceptionType"] = _exception_name(exception)
        else:
            canonical_return = self._canonicalizer.digest(return_value, source_path=state.file_path)
            record["returnDigest"] = canonical_return.digest
            record["returnRendered"] = canonical_return.rendered
        self._write(record)
        self._module_calls[_module_name(state.file_path)] += 1

    def _pop_stack(self, state: CallState) -> None:
        stack = self._stacks[state.thread_id]
        if stack and stack[-1] == state.call_id:
            stack.pop()

    def _write(self, record: dict[str, object]) -> None:
        self._stream.write(json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n")
        self._stream.flush()
        self._written += 1

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
            self._stream.close()
            self._write_manifest()

    def _write_manifest(self) -> None:
        by_module: dict[str, list[Member]] = defaultdict(list)
        for member in self._members.values():
            by_module[member.module].append(member)
        with self.manifest_path.open("w", encoding="utf-8", newline="\n") as stream:
            _write_line(stream, {"kind": "run", "schema": "realdiff.trace/1", "language": "python"})
            for module in sorted(by_module):
                members = sorted(by_module[module], key=lambda item: item.method)
                patched = sum(member.status == "Patched" for member in members)
                _write_line(
                    stream,
                    {
                        "kind": "assembly",
                        "assembly": module,
                        "discovery": "SysMonitoring",
                        "scanned": True,
                        "instrumented": patched > 0,
                        "patchedMembers": patched,
                        "discoveredMembers": len(members),
                        "skippedMembers": len(members) - patched,
                        "patchFailedMembers": 0,
                        "queuedAtMs": 0,
                        "patchedAtMs": 0,
                        "tracedCalls": self._module_calls[module],
                        "membersWithExactSource": patched,
                        "exactSourcePercent": 100,
                        "sourceRule": "ratio",
                    },
                )
                for member in members:
                    record: dict[str, object] = {
                        "kind": "member",
                        "assembly": member.module,
                        "method": member.method,
                        "filePath": member.file_path,
                        "line": member.line,
                        "status": member.status,
                        "returnKind": member.return_kind,
                        "sourceResolution": "debugInfo",
                    }
                    if member.skip_reason is not None:
                        record["skipReason"] = member.skip_reason
                    if member.detail is not None:
                        record["detail"] = member.detail
                    _write_line(stream, record)
            _write_line(
                stream,
                {
                    "kind": "digest",
                    "valuesDigested": self._digest_statistics.values_digested,
                    "depthLimited": self._digest_statistics.depth_limited,
                    "blocklisted": self._digest_statistics.blocklisted,
                    "errored": self._digest_statistics.errored,
                    "renderedTruncated": self._digest_statistics.rendered_truncated,
                    "unreadableFields": self._digest_statistics.unreadable_fields,
                    "ambiguousMapEntries": self._digest_statistics.ambiguous_map_entries,
                },
            )
            _write_line(
                stream,
                {"kind": "writer", "enqueued": self._written, "written": self._written, "dropped": 0, "capacity": 1},
            )


def _write_line(stream, record: dict[str, object]) -> None:
    stream.write(json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n")


def _module_name(file_path: str) -> str:
    path = Path(file_path)
    return ".".join(path.with_suffix("").parts)


def _method_name(file_path: str, code: CodeType) -> str:
    return f"{_module_name(file_path)}::{code.co_qualname}"


def _return_kind(code: CodeType) -> str:
    if code.co_flags & 0x200:
        return "AsyncGenerator"
    if code.co_flags & 0x80:
        return "Coroutine"
    if code.co_flags & 0x20:
        return "Generator"
    return "Sync"


def _exception_name(exception: BaseException) -> str:
    type_ = type(exception)
    return f"{type_.__module__}.{type_.__qualname__}"


def _capture_arguments(code: CodeType, frame: FrameType) -> dict[str, object]:
    count = code.co_argcount + code.co_kwonlyargcount
    names = list(code.co_varnames[:count])
    next_index = count
    if code.co_flags & 0x04:
        names.append(code.co_varnames[next_index])
        next_index += 1
    if code.co_flags & 0x08:
        names.append(code.co_varnames[next_index])
    locals_ = frame.f_locals
    return {name: dict.get(locals_, name, MISSING) for name in names}