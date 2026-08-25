#!/usr/bin/env bash
set -euo pipefail

repo="${1:-/github/workspace}"
work="${2:-/github/workspace/.behaviordiff/work}"
findings="${3:-/github/workspace/.behaviordiff/artifacts/findings.json}"
cache_dir="${4:-/github/workspace/.behaviordiff/cache}"
cache_retention="${5:-1d}"
gate="${6:-warn-only}"
post="${7:-true}"
strict="${8:-false}"
engine="${9:-csharp}"

mkdir -p "$(dirname "$findings")" "$cache_dir"

analysis_args=("$repo" --ci=github --work "$work" --findings "$findings"
    --cache-dir "$cache_dir" --cache-retention "$cache_retention" --engine "$engine")
if [[ "${strict,,}" == "true" ]]; then
    analysis_args+=(--strict)
fi

set +e
behaviordiff "${analysis_args[@]}"
analysis_exit=$?
set -e

status="missing"
verdict="could_not_analyze"
if [[ -f "$findings" ]]; then
    status="$(node -e 'const f=require(process.argv[1]); process.stdout.write(String(f.status))' "$findings")"
    verdict="$(node -e 'const f=require(process.argv[1]); process.stdout.write(String(f.verdict))' "$findings")"
fi

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
        echo "analysis-exit=$analysis_exit"
        echo "findings=$findings"
        echo "status=$status"
        echo "verdict=$verdict"
    } >> "$GITHUB_OUTPUT"
fi

if [[ ! -f "$findings" ]]; then
    echo "BehaviorDiff exited $analysis_exit without writing $findings" >&2
    exit "$analysis_exit"
fi

if [[ "${post,,}" == "true" ]]; then
    exec behaviordiff post --provider=github --findings "$findings" --gate "$gate"
fi

if (( analysis_exit == 0 || analysis_exit == 1 )); then
    exit 0
fi

exit "$analysis_exit"
