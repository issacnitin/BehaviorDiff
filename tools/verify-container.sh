#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "usage: $0 <image>" >&2
    exit 2
fi

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="$1"
state="$(mktemp -d)"
mkdir -p "$state/workspace" "$state/cache" "$state/baseline" "$state/metrics"

docker_path() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s\n' "$1"
    fi
}

cleanup() {
    MSYS_NO_PATHCONV=1 docker run --rm \
        --entrypoint /bin/bash \
        --volume "$(docker_path "$state"):/proof" \
        "$image" -c 'rm -rf /proof/workspace /proof/cache /proof/baseline /proof/metrics' \
        >/dev/null 2>&1 || true
    rm -rf "$state" 2>/dev/null || true
}

trap 'status=$?; trap - EXIT; cleanup; exit "$status"' EXIT
trap 'status=$?; echo "::error file=tools/verify-container.sh,line=$LINENO::Container proof failed: $BASH_COMMAND (exit $status)"; exit "$status"' ERR

read_metric() {
    local file="$1"
    local name="$2"
    sed -n "s/^$name=//p" "$file"
}

run_container() {
    local phase="$1"
    MSYS_NO_PATHCONV=1 docker run --rm \
        --entrypoint /bin/bash \
        --volume "$(docker_path "$repo"):/source:ro" \
        --volume "$(docker_path "$state/workspace"):/proof/workspace" \
        --volume "$(docker_path "$state/cache"):/proof/cache" \
        --volume "$(docker_path "$state/baseline"):/proof/baseline" \
        --volume "$(docker_path "$state/metrics"):/proof/metrics" \
        --env BEHAVIORDIFF_CONTAINER_PROOF_ROOT=/proof/workspace \
        --env BEHAVIORDIFF_CONTAINER_CACHE=/proof/cache \
        --env BEHAVIORDIFF_CONTAINER_BASELINE=/proof/baseline/baseline.yml \
        --env BEHAVIORDIFF_CONTAINER_METRICS=/proof/metrics \
        "$image" /source/tools/verify-container-fixtures.sh "$phase"
}

run_phase() {
    local phase="$1"
    local started
    local finished
    local host_ms
    local metric_file="$state/metrics/$phase.env"
    started="$(date +%s%N)"
    run_container "$phase"
    finished="$(date +%s%N)"
    host_ms=$(( (finished - started) / 1000000 ))

    [[ -f "$metric_file" ]] || { echo "$phase container did not write metrics" >&2; exit 1; }
    local measured_ms
    local saved_ms
    local cache_status
    local actionable_members
    local suppressed_members
    measured_ms="$(read_metric "$metric_file" measured_ms)"
    saved_ms="$(read_metric "$metric_file" saved_ms)"
    cache_status="$(read_metric "$metric_file" cache_status)"
    actionable_members="$(read_metric "$metric_file" actionable_members)"
    suppressed_members="$(read_metric "$metric_file" suppressed_members)"
    [[ "$measured_ms" =~ ^[0-9]+$ && "$saved_ms" =~ ^[0-9]+$ ]] || {
        echo "$phase container metrics are not numeric" >&2
        exit 1
    }
    (( measured_ms > 0 && host_ms >= measured_ms )) || {
        echo "$phase host timing ${host_ms}ms is smaller than reported measured timing ${measured_ms}ms" >&2
        exit 1
    }

    printf -v "${phase}_host_ms" '%d' "$host_ms"
    printf -v "${phase}_measured_ms" '%d' "$measured_ms"
    printf -v "${phase}_saved_ms" '%d' "$saved_ms"
    printf -v "${phase}_cache_status" '%s' "$cache_status"
    printf -v "${phase}_actionable_members" '%s' "$actionable_members"
    printf -v "${phase}_suppressed_members" '%s' "$suppressed_members"
    echo "CONTAINER_NODE_TIMING phase=$phase host_ms=$host_ms measured_ms=$measured_ms overhead_ms=$((host_ms - measured_ms)) cache=$cache_status saved_ms=$saved_ms"
}

run_phase cold
[[ "$cold_cache_status" == miss ]] || { echo "cold container did not report a cache miss" >&2; exit 1; }
cache_entries="$(find "$state/cache" -name metadata.json -type f | wc -l | tr -d ' ')"
(( cache_entries > 0 )) || { echo "cold container did not persist trace cache metadata" >&2; exit 1; }
grep -q '^schema: behaviordiff.baseline/2$' "$state/baseline/baseline.yml"

run_phase warm
[[ "$warm_cache_status" == hit ]] || { echo "warm container did not reuse the trace cache" >&2; exit 1; }
(( warm_saved_ms > 0 )) || { echo "warm container reported no saved base-trace time" >&2; exit 1; }
[[ "$warm_actionable_members" == 0 && "$warm_suppressed_members" -gt 0 ]] || {
    echo "warm container did not reuse the mounted baseline" >&2
    exit 1
}
run_container java

echo "CONTAINER_PERSISTENT_MOUNTS cache_entries=$cache_entries baseline_schema=behaviordiff.baseline/2 suppressed_members=$warm_suppressed_members"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    cat >> "$GITHUB_STEP_SUMMARY" <<EOF
### Container cold/warm proof

| phase | host wall time | measured engine time | container overhead | cache | reported base time saved |
| --- | ---: | ---: | ---: | --- | ---: |
| cold | ${cold_host_ms} ms | ${cold_measured_ms} ms | $((cold_host_ms - cold_measured_ms)) ms | ${cold_cache_status} | ${cold_saved_ms} ms |
| warm | ${warm_host_ms} ms | ${warm_measured_ms} ms | $((warm_host_ms - warm_measured_ms)) ms | ${warm_cache_status} | ${warm_saved_ms} ms |

Persistent cache entries: ${cache_entries}. Mounted schema-v2 baseline suppressed ${warm_suppressed_members} member(s) in the fresh warm container.
EOF
fi
echo 'VERIFY_CONTAINER: PASS'
