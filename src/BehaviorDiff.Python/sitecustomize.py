import os


if os.environ.get("BEHAVIORDIFF_TRACE"):
    from behaviordiff_python.monitor import install

    install()