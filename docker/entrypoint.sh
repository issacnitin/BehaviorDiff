#!/usr/bin/env bash
set -euo pipefail

git config --global --add safe.directory '*' >/dev/null 2>&1 || true

if [[ "${1:-}" == "__action" ]]; then
    shift
    exec /usr/local/bin/realdiff-action "$@"
fi

exec /usr/local/bin/realdiff "$@"
