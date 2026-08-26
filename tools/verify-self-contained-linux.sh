#!/usr/bin/env bash
set -euo pipefail

command -v dotnet >/dev/null 2>&1 && { echo 'clean Linux proof unexpectedly has dotnet' >&2; exit 1; }
[[ ! -e /usr/share/dotnet ]] || { echo 'clean Linux proof unexpectedly has /usr/share/dotnet' >&2; exit 1; }

root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT
git config --global user.name 'RealDiff Release Proof'
git config --global user.email 'release-proof@realdiff.invalid'

cat > "$root/mock-github.js" <<'NODE'
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
    } else if (request.method === 'POST' && request.url.endsWith('/issues/1/comments')) {
      fs.writeFileSync(capture, body);
      response.end('{"id":4242}');
    } else {
      response.statusCode = 404;
      response.end('{"message":"unexpected request"}');
    }
  });
});
server.listen(0, '127.0.0.1', () => console.log(server.address().port));
NODE

verify_result() {
    local language="$1"
    local work="$2"
    local findings="$3"
    local comment="$4"
    local events
    events="$(find "$work" -name 'run.*.ndjson' ! -name '*.manifest.ndjson' -type f -exec cat {} + | grep -cve '^$')"
    (( events > 0 )) || { echo "$language release proof produced zero events" >&2; exit 1; }
    node - "$language" "$findings" "$comment" "$events" <<'NODE'
const fs = require('node:fs');
const [language, findingsPath, commentPath, eventText] = process.argv.slice(2);
const findings = JSON.parse(fs.readFileSync(findingsPath, 'utf8'));
const payload = JSON.parse(fs.readFileSync(commentPath, 'utf8'));
const events = Number(eventText);
if (findings.status !== 'analyzed' || !(findings.summary.unexpectedMembers > 0)) {
  throw new Error(`${language} findings were ${findings.status}/${findings.verdict}`);
}
if (typeof payload.body !== 'string' || payload.body.length < 100
    || !payload.body.includes('## RealDiff:')
    || !payload.body.includes('<!-- realdiff:github:pr:1:summary -->')) {
  throw new Error(`${language} rendered comment contract failed`);
}
console.log(`SELF_CONTAINED_${language.toUpperCase()} events=${events} findings=${findings.summary.unexpectedMembers} commentBytes=${Buffer.byteLength(payload.body)}`);
NODE
}

run_proof() {
    local language="$1"
    local fixture="$2"
  local base_setup="$3"
  local mutation="$4"
    local repo="$root/${language}-repo"
    local work="$root/${language}-work"
    local findings="$work/findings.json"
    local event="$root/${language}-event.json"
    local comment="$root/${language}-comment.json"

    cp -a "/fixtures/$fixture" "$repo"
    rm -rf "$repo/node_modules" "$repo/target"
    (cd "$repo" && eval "$base_setup")
    git -C "$repo" init --initial-branch=main --quiet
    git -C "$repo" add .
    git -C "$repo" commit --quiet -m 'base'
    local base
    base="$(git -C "$repo" rev-parse HEAD)"
    (cd "$repo" && eval "$mutation")
    git -C "$repo" add .
    git -C "$repo" commit --quiet -m 'behavior change'
    local pr
    pr="$(git -C "$repo" rev-parse HEAD)"
    printf '{"number":1,"pull_request":{"base":{"sha":"%s","repo":{"full_name":"realdiff/release-proof","fork":false}},"head":{"sha":"%s","repo":{"full_name":"realdiff/release-proof","fork":false}}}}\n' "$base" "$pr" > "$event"

    set +e
    GITHUB_EVENT_PATH="$event" GITHUB_REPOSITORY=realdiff/release-proof \
      REALDIFF_EXCLUDE_NAMESPACES="$(if [[ "$language" == node ]]; then printf 'src/sorting/rule-ordering.js'; fi)" \
      realdiff "$repo" --ci=github --work "$work" --findings "$findings" \
      --no-baseline --strict --keep-traces 1d
    local analysis_exit=$?
    set -e
    [[ $analysis_exit -eq 1 ]] || { echo "$language analysis exited $analysis_exit, expected findings exit 1" >&2; exit 1; }

    coproc MOCK_API { node "$root/mock-github.js" "$comment"; }
    local api_pid=$MOCK_API_PID
    read -r api_port <&"${MOCK_API[0]}"
    set +e
    GITHUB_EVENT_PATH="$event" GITHUB_REPOSITORY=realdiff/release-proof \
      GITHUB_TOKEN=release-proof-token GITHUB_API_URL="http://127.0.0.1:$api_port" \
      realdiff post --provider=github --findings "$findings" --gate warn-only
    local post_exit=$?
    set -e
    kill "$api_pid" 2>/dev/null || true
    wait "$api_pid" 2>/dev/null || true
    [[ $post_exit -eq 0 ]] || { echo "$language comment post exited $post_exit" >&2; exit 1; }
    verify_result "$language" "$work" "$findings" "$comment"
}

run_proof node NodeSortDemo ':' \
  "sed -i 's/a.priority - b.priority/(a.priority - b.priority) || a.code.localeCompare(b.code)/' src/sorting/rule-ordering.js"
run_proof go GoReference \
  "mkdir src; mv ./*.go src/; printf 'package goreference\n\nconst addBias = 0\n\nfunc ConfigMarker() int { return 0 }\n' > src/config.go; sed -i 's/return left + right/return left + right + addBias/;s/func ExerciseCore(seed int) int {/func ExerciseCore(seed int) int { ConfigMarker()/' src/operations.go" \
  "sed -i 's/addBias = 0/addBias = 1/' src/config.go"

echo 'SELF_CONTAINED_CLEAN_LINUX: PASS dotnet=absent languages=2'