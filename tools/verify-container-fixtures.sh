#!/usr/bin/env bash
set -euo pipefail

root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT

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
    initialize_fixture NodeSortDemo "$repo"
    local base
    base="$(git -C "$repo" rev-parse HEAD)"
    sed -i 's|a.priority - b.priority|(a.priority - b.priority) || a.code.localeCompare(b.code)|' \
        "$repo/src/sorting/rule-ordering.js"
    git -C "$repo" add src/sorting/rule-ordering.js
    git -C "$repo" commit --quiet -m 'pr: make priority ties deterministic by code'
    local pr
    pr="$(git -C "$repo" rev-parse HEAD)"

    set +e
    BEHAVIORDIFF_EXCLUDE_NAMESPACES=src/sorting/rule-ordering.js \
        behaviordiff "$repo" --base "$base" --pr "$pr" --work "$work" \
        --findings "$work/findings.json"
    local exit_code=$?
    set -e
    [[ $exit_code -eq 1 ]] || { echo "Node container analysis exited $exit_code, expected 1" >&2; exit 1; }
    assert_analyzed_findings "$work/findings.json"
    echo 'CONTAINER_NODE_ANALYSIS: PASS'
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