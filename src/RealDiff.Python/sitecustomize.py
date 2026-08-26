import os


if os.environ.get("REALDIFF_TRACE"):
    plugins = [item for item in os.environ.get("PYTEST_PLUGINS", "").split(",") if item]
    if "realdiff_python.pytest_plugin" not in plugins:
        plugins.append("realdiff_python.pytest_plugin")
        os.environ["PYTEST_PLUGINS"] = ",".join(plugins)

    from realdiff_python.monitor import install
    from realdiff_python.runtime import Runtime, set_active_runtime

    runtime = Runtime.from_environment()
    set_active_runtime(runtime)
    install(runtime.on_event)