#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "usage: $0 <image>" >&2
    exit 2
fi

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
docker run --rm \
    --entrypoint /bin/bash \
    --volume "$repo:/source:ro" \
    "$1" /source/tools/verify-container-fixtures.sh
