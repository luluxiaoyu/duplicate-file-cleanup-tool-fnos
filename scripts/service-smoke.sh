#!/usr/bin/env bash
set -euo pipefail
publish_dir=$(cd "${1:?Pass the Linux AOT publish directory}" && pwd)
web_dir=$(cd "${2:?Pass the frontend dist directory}" && pwd)
work=$(mktemp -d /tmp/duplicate-service-smoke.XXXXXX)
child=
cleanup() {
    if [ -n "$child" ]; then kill -TERM "$child" 2>/dev/null || true; wait "$child" 2>/dev/null || true; fi
    rm -rf "$work"
}
trap cleanup EXIT
mkdir "$work/target" "$work/data"
cp -R "$publish_dir" "$work/target/bin"
cp -R "$web_dir" "$work/target/www"
chown -R nobody "$work"
chmod 755 "$work"
runuser -u nobody -- env TRIM_APPDEST="$work/target" TRIM_PKGVAR="$work/data" \
    "$work/target/bin/duplicate-cleanup" >"$work/service.log" 2>&1 &
child=$!
for i in $(seq 1 100); do
    [ -f "$work/data/server.ready" ] && break
    sleep 0.1
done
[ -f "$work/data/server.ready" ] || { cat "$work/service.log"; exit 1; }
base=http://localhost/app/duplicate-file-cleanup-tool-fnos/api
status=$(curl -sS --unix-socket "$work/target/app.sock" -o "$work/body" -w '%{http_code}' "$base/health")
[ "$status" = 401 ]
status=$(curl -sS --unix-socket "$work/target/app.sock" -H 'X-Trim-Userid: 1000' -o "$work/body" -w '%{http_code}' "$base/health")
[ "$status" = 200 ]
status=$(curl -sS --unix-socket "$work/target/app.sock" -H 'X-Trim-Userid: 1000' -o "$work/body" -w '%{http_code}' "$base/tasks")
[ "$status" = 200 ]
python3 -c 'import json,sys; assert json.load(open(sys.argv[1]))["tasks"] == []' "$work/body"
status=$(curl -sS --unix-socket "$work/target/app.sock" -H 'X-Trim-Userid: 1000' -X POST -o "$work/body" -w '%{http_code}' "$base/tasks/invalid/cancel")
[ "$status" = 403 ]
status=$(curl -sS --unix-socket "$work/target/app.sock" -H 'X-Trim-Userid: 1000' -o "$work/body" -w '%{http_code}' "$base/folders")
[ "$status" = 503 ] # fnOS socket/token unavailable: fail closed, never return arbitrary roots.
echo 'PASS AOT daemon startup, UDS, identity, CSRF, JSON and missing-platform refusal'
