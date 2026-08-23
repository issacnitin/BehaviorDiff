#!/usr/bin/env bash
set -euo pipefail
trap 'status=$?; echo "::error file=.github,line=8::Container fixture failed at line $LINENO: $BASH_COMMAND (exit $status)"; exit $status' ERR

root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT

git config --global --add safe.directory '*'

for command in dotnet java mvn node npm go git behaviordiff behaviordiff-go-rewrite; do
    command -v "$command" >/dev/null || { echo "missing image command: $command" >&2; exit 1; }
done

initialize_fixture() {
    local sample="$1"
    local destination="$2"
    mkdir -p "$destination"
    cp -a "/source/samples/$sample/." "$destination/"
    rm -rf "$destination/node_modules" "$destination/target"
    git -C "$destination" init --initial-branch=main --quiet
    git -C "$destination" config user.name 'BehaviorDiff Container Proof'
    git -C "$destination" config user.email 'container-proof@behaviordiff.invalid'
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

run_node_proof() {
    local repo="$root/node-repo"
    local work="$root/node-work"
    local findings="$work/findings.json"
    local event="$root/github-event.json"
    local output="$root/github-output.txt"
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
{"number":1,"pull_request":{"base":{"sha":"$base"},"head":{"sha":"$pr"}}}
JSON

    GITHUB_EVENT_PATH="$event" \
    GITHUB_REPOSITORY=behaviordiff/container-proof \
    GITHUB_WORKSPACE="$repo" \
    GITHUB_OUTPUT="$output" \
    BEHAVIORDIFF_EXCLUDE_NAMESPACES=src/sorting/rule-ordering.js \
        /usr/local/bin/behaviordiff-entrypoint __action \
        "$repo" "$work" "$findings" "$root/node-cache" 1d warn-only false true

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
    echo 'CONTAINER_NODE_ACTION_ANALYSIS: PASS'
}

run_java_proof() {
    local repo="$root/java-repo"
    local work="$root/java-work"
    initialize_fixture JavaSortDemo "$repo"
    local base
    base="$(git -C "$repo" rev-parse HEAD)"
    sed -i \
        's|ordered.sort(Comparator.comparingInt(RuleOrdering::priority));|ordered.sort(Comparator.comparingInt(RuleOrdering::priority).thenComparing(RuleOrdering::code));|' \
        "$repo/src/main/java/io/behaviordiff/demo/sorting/RuleOrdering.java"
    git -C "$repo" add src/main/java/io/behaviordiff/demo/sorting/RuleOrdering.java
    git -C "$repo" commit --quiet -m 'pr: make priority ties deterministic by code'
    local pr
    pr="$(git -C "$repo" rev-parse HEAD)"

    set +e
    BEHAVIORDIFF_EXCLUDE_NAMESPACES=io.behaviordiff.demo.sorting \
        behaviordiff "$repo" --base "$base" --pr "$pr" --work "$work" \
        --findings "$work/findings.json"
    local exit_code=$?
    set -e
    [[ $exit_code -eq 1 ]] || { echo "Java container analysis exited $exit_code, expected 1" >&2; exit 1; }
    assert_analyzed_findings "$work/findings.json"
    echo 'CONTAINER_JAVA_ANALYSIS: PASS'
}

run_node_proof
run_java_proof
echo 'VERIFY_CONTAINER_FIXTURES: PASS'