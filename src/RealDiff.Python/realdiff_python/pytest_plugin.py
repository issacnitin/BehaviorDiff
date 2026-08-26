from __future__ import annotations

import pytest

from .runtime import active_runtime


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_call(item):
    runtime = active_runtime()
    code = getattr(getattr(item, "obj", None), "__code__", None)
    if runtime is None or code is None:
        yield
        return
    runtime.register_test_root(code, item.nodeid)
    try:
        yield
    finally:
        runtime.clear_test_root()