#!/usr/bin/env bash
set -euo pipefail
trap 'status=$?; echo "::error file=.github,line=8::Container fixture failed at line $LINENO: $BASH_COMMAND (exit $status)"; exit $status' ERR

owns_root=false
if [[ -n "${REALDIFF_CONTAINER_PROOF_ROOT:-}" ]]; then
    root="$REALDIFF_CONTAINER_PROOF_ROOT"
else
    root="$(mktemp -d)"
    owns_root=true
fi
cache="${REALDIFF_CONTAINER_CACHE:-$root/cache}"
baseline="${REALDIFF_CONTAINER_BASELINE:-$root/baseline/baseline.yml}"
metrics="${REALDIFF_CONTAINER_METRICS:-$root/metrics}"
mkdir -p "$root" "$cache" "$(dirname "$baseline")" "$metrics"
trap 'if [[ "$owns_root" == true ]]; then rm -rf "$root"; fi' EXIT

git config --global --add safe.directory '*'

for command in dotnet java mvn node npm go cargo rustc git realdiff realdiff-go-rewrite; do
    command -v "$command" >/dev/null || { echo "missing image command: $command" >&2; exit 1; }
done

initialize_fixture() {
    local sample="$1"
    local destination="$2"
    mkdir -p "$destination"
    cp -a "/source/samples/$sample/." "$destination/"
    rm -rf "$destination/node_modules" "$destination/target"
    git -C "$destination" init --initial-branch=main --quiet
    git -C "$destination" config user.name 'RealDiff Container Proof'
    git -C "$destination" config user.email 'container-proof@realdiff.invalid'
    git -C "$destination" add .
    git -C "$destination" commit --quiet -m 'base: stable priority ordering'
}

assert_analyzed_findings() {
    local findings="$1"
    node - "$findings" <<'NODE'
const fs = require('node:fs');
const artifact = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
if (artifact.status !== 'analyzed' || artifact.verdict !== 'findings') {
  throw new Error(`expected analyzed findings, got ${artifact.status}/${artifact.verdict}`);
}
if (!(artifact.summary.unexpectedMembers > 0)) {
  throw new Error('expected at least one unexpected member');
}
NODE
}

write_metrics() {
        local phase="$1"
        local findings="$2"
        local expected_cache="$3"
        node - "$phase" "$findings" "$metrics/$phase.env" "$expected_cache" <<'NODE'
const fs = require('node:fs');
const [phase, findingsPath, outputPath, expectedCache] = process.argv.slice(2);
const artifact = JSON.parse(fs.readFileSync(findingsPath, 'utf8'));
const actualCache = artifact.baseTraceCache?.status;
const measured = artifact.timings?.measuredTotalMilliseconds;
const saved = artifact.baseTraceCache?.savedWallClockMilliseconds;
if (actualCache !== expectedCache) {
    throw new Error(`${phase} cache status was ${actualCache}, expected ${expectedCache}`);
}
if (!Number.isInteger(measured) || measured <= 0 || !Number.isInteger(saved) || saved < 0) {
    throw new Error(`${phase} findings did not contain valid timing/cache metrics`);
}
fs.writeFileSync(outputPath, [
    `measured_ms=${measured}`,
    `saved_ms=${saved}`,
    `cache_status=${actualCache}`,
    `actionable_members=${artifact.summary.actionableUnexpectedMembers ?? artifact.summary.unexpectedMembers}`,
    `suppressed_members=${artifact.summary.suppressedMembers ?? 0}`,
    '',
].join('\n'));
NODE
}

run_node_proof() {
    local repo="$root/node-repo"
        local work="$root/node-cold-work"
    local findings="$work/findings.json"
    local event="$root/github-event.json"
    local output="$root/github-output.txt"
    local comment="$root/github-comment.json"
    local mock_api="$root/github-api.js"
    initialize_fixture NodeSortDemo "$repo"
    local base
    base="$(git -C "$repo" rev-parse HEAD)"
    sed -i 's@a.priority - b.priority@(a.priority - b.priority) || a.code.localeCompare(b.code)@' \
        "$repo/src/sorting/rule-ordering.js"
    git -C "$repo" add src/sorting/rule-ordering.js
    git -C "$repo" commit --quiet -m 'pr: make priority ties deterministic by code'
    local pr
    pr="$(git -C "$repo" rev-parse HEAD)"

        cat > "$event" <<JSON
{"number":1,"pull_request":{"base":{"sha":"$base","repo":{"full_name":"realdiff/container-proof","fork":false}},"head":{"sha":"$pr","repo":{"full_name":"realdiff/container-proof","fork":false}}}}
JSON

        cat > "$mock_api" <<'NODE'
const fs = require('node:fs');
const http = require('node:http');

const capture = process.argv[2];
const server = http.createServer((request, response) => {
    let body = '';
    request.setEncoding('utf8');
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
        response.setHeader('Content-Type', 'application/json');
        if (request.method === 'GET') {
            response.end('[]');
            return;
        }
        if (request.method === 'POST' && request.url.endsWith('/issues/1/comments')) {
            fs.writeFileSync(capture, body);
            response.end('{"id":4242}');
            return;
        }
        response.statusCode = 404;
        response.end('{"message":"unexpected mock GitHub request"}');
    });
});
server.listen(0, '127.0.0.1', () => console.log(server.address().port));
NODE

        (
            coproc GITHUB_API { node "$mock_api" "$comment"; }
            api_pid=$GITHUB_API_PID
            trap 'kill "$api_pid" 2>/dev/null || true; wait "$api_pid" 2>/dev/null || true' EXIT
            read -r api_port <&"${GITHUB_API[0]}"
            set +e
            GITHUB_EVENT_PATH="$event" \
            GITHUB_REPOSITORY=realdiff/container-proof \
            GITHUB_TOKEN=container-proof-token \
            GITHUB_API_URL="http://127.0.0.1:$api_port" \
            ANTHROPIC_API_KEY= \
            GITHUB_WORKSPACE="$repo" \
            GITHUB_OUTPUT="$output" \
            REALDIFF_EXCLUDE_NAMESPACES=src/sorting/rule-ordering.js \
                /usr/local/bin/realdiff-entrypoint __action \
                "$repo" "$work" "$findings" "$cache" 1d warn-only true true
            action_exit=$?
            set -e
            [[ $action_exit -eq 0 ]] || { echo "Node container action exited $action_exit" >&2; exit 1; }
        )

    assert_analyzed_findings "$findings"
    grep -qx 'analysis-exit=1' "$output"
    grep -qx 'status=analyzed' "$output"
    grep -qx 'verdict=findings' "$output"
        node - "$findings" <<'NODE'
const artifact = require(process.argv[2]);
if (artifact.commentPolicy?.mode !== 'strict') {
    throw new Error(`expected strict comment policy, got ${artifact.commentPolicy?.mode}`);
}
NODE
        node - "$comment" "$findings" <<'NODE'
const fs = require('node:fs');
const payload = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const artifact = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
const member = artifact.members.find(item => item.attribution === 'unexpected');
const token = member?.memberName.split(/[.#]/).at(-1)?.split('(')[0];
if (typeof payload.body !== 'string' || payload.body.length < 100) {
    throw new Error('production GitHub comment was not captured');
}
if (!payload.body.includes('## RealDiff:')
        || !payload.body.includes('<!-- realdiff:github:pr:1:summary -->')
        || !token || !payload.body.includes(token)) {
    throw new Error(`rendered comment lost its heading, marker, or member token ${token}`);
}
console.log(`CONTAINER_NODE_RENDERED_COMMENT bytes=${Buffer.byteLength(payload.body)} member=${token}`);
NODE
    write_metrics cold "$findings" miss
    realdiff baseline write --findings "$findings" --output "$baseline" --no-expiry
    grep -q '^schema: realdiff.baseline/2$' "$baseline"
    echo 'CONTAINER_NODE_ACTION_ANALYSIS: PASS'
}

run_node_warm_proof() {
    local repo="$root/node-repo"
    local work="$root/node-warm-work"
    local findings="$work/findings.json"
    [[ -d "$repo/.git" ]] || { echo "persisted Node fixture is missing" >&2; exit 1; }
    [[ -f "$baseline" ]] || { echo "persisted baseline is missing" >&2; exit 1; }
    find "$cache" -name metadata.json -print -quit | grep -q . || {
        echo "persisted trace cache metadata is missing" >&2
        exit 1
    }

    local base
    local pr
    base="$(git -C "$repo" rev-list --max-parents=0 HEAD)"
    pr="$(git -C "$repo" rev-parse HEAD)"
    set +e
    REALDIFF_EXCLUDE_NAMESPACES=src/sorting/rule-ordering.js \
        realdiff "$repo" --base "$base" --pr "$pr" --work "$work" \
        --findings "$findings" --cache-dir "$cache" --cache-retention 1d \
        --baseline "$baseline" --strict
    local exit_code=$?
    set -e
    [[ $exit_code -eq 0 ]] || { echo "Warm Node container analysis exited $exit_code, expected 0" >&2; exit 1; }
    assert_analyzed_findings "$findings"
    node - "$findings" <<'NODE'
const artifact = require(process.argv[2]);
if (artifact.summary.actionableUnexpectedMembers !== 0
    || !(artifact.summary.suppressedMembers > 0)) {
  throw new Error(`persisted baseline did not suppress warm findings: ${JSON.stringify(artifact.summary)}`);
}
NODE
    write_metrics warm "$findings" hit
    echo 'CONTAINER_NODE_PERSISTENT_CACHE_BASELINE: PASS'
}

run_java_proof() {
    local repo="$root/java-repo"
    local work="$root/java-work"
    initialize_fixture JavaSortDemo "$repo"
    local base
    base="$(git -C "$repo" rev-parse HEAD)"
    sed -i \
        's|ordered.sort(Comparator.comparingInt(RuleOrdering::priority));|ordered.sort(Comparator.comparingInt(RuleOrdering::priority).thenComparing(RuleOrdering::code));|' \
        "$repo/src/main/java/io/realdiff/demo/sorting/RuleOrdering.java"
    git -C "$repo" add src/main/java/io/realdiff/demo/sorting/RuleOrdering.java
    git -C "$repo" commit --quiet -m 'pr: make priority ties deterministic by code'
    local pr
    pr="$(git -C "$repo" rev-parse HEAD)"

    set +e
    REALDIFF_EXCLUDE_NAMESPACES=io.realdiff.demo.sorting \
        realdiff "$repo" --base "$base" --pr "$pr" --work "$work" \
        --findings "$work/findings.json"
    local exit_code=$?
    set -e
    [[ $exit_code -eq 1 ]] || { echo "Java container analysis exited $exit_code, expected 1" >&2; exit 1; }
    assert_analyzed_findings "$work/findings.json"
    echo 'CONTAINER_JAVA_ANALYSIS: PASS'
}

run_rust_proof() {
    local repo="$root/rust-repo"
    local work="$root/rust-work"
    local findings="$work/findings.json"
    local output="$root/rust-output.log"
    initialize_fixture RustSortDemo "$repo"
    local base
    base="$(git -C "$repo" rev-parse HEAD)"
    sed -i 's/PRIORITY_BIAS: i32 = 0/PRIORITY_BIAS: i32 = 5/' "$repo/src/config.rs"
    git -C "$repo" add src/config.rs
    git -C "$repo" commit --quiet -m 'pr: change priority bias'
    local pr
    pr="$(git -C "$repo" rev-parse HEAD)"

    set +e
    realdiff "$repo" --base "$base" --pr "$pr" --work "$work" \
        --findings "$findings" --no-baseline --strict --keep-traces 1d 2>&1 | tee "$output"
    local exit_code=${PIPESTATUS[0]}
    set -e
    [[ $exit_code -eq 1 ]] || { echo "Rust container analysis exited $exit_code, expected 1" >&2; exit 1; }
    grep -Eq '^  engine: rust$' "$output"
    grep -Eq '^  rust tracer: /opt/realdiff/tracers/rust/linux-(x64|arm64)/realdiff-rust-rewrite$' "$output"
    assert_analyzed_findings "$findings"
    local events
    events="$(find "$work/base_run1" -name 'run.*.ndjson' ! -name '*.manifest.ndjson' -type f -exec cat {} + | grep -cve '^$')"
    (( events > 0 )) || { echo 'Rust container trace input is empty' >&2; exit 1; }
    node - "$findings" "$events" <<'NODE'
const artifact = require(process.argv[2]);
const events = Number(process.argv[3]);
if (artifact.summary.unexpectedMembers !== 1
    || artifact.summary.editedFiles !== 1
    || artifact.summary.tracedMembers !== 0
    || artifact.summary.untestedMembers !== 1) {
  throw new Error(`Rust container findings shape differs for ${events} events: ${JSON.stringify(artifact.summary)}`);
}
NODE
    echo "CONTAINER_RUST_DEFAULT_TRACER events=$events: PASS"
}

phase="${1:-all}"
case "$phase" in
    cold)
        run_node_proof
        ;;
    warm)
        run_node_warm_proof
        ;;
    java)
        run_java_proof
        ;;
    rust)
        run_rust_proof
        ;;
    all)
        run_node_proof
        run_java_proof
        run_node_warm_proof
        run_rust_proof
        ;;
    *)
        echo "usage: $0 [cold|warm|java|rust|all]" >&2
        exit 2
        ;;
esac
echo 'VERIFY_CONTAINER_FIXTURES: PASS'