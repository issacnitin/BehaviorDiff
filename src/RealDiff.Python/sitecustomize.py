import os


if os.environ.get("REALDIFF_TRACE"):
    from realdiff_python.monitor import install
    from realdiff_python.runtime import Runtime

    runtime = Runtime.from_environment()
    install(runtime.on_event)