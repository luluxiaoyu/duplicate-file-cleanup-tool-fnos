#!/usr/bin/env bash
set -euo pipefail
publish_dir=$(cd "${1:?Pass the Linux AOT publish directory}" && pwd)
web_dir=$(cd "${2:?Pass the frontend dist directory}" && pwd)
work=$(mktemp -d /tmp/duplicate-service-smoke.XXXXXX)
control="$(cd "$(dirname "$0")/../cmd" && pwd)/main"
started=false
app_control() {
    # App Center passes symlinked paths and can inherit a root-only working directory.
    (cd "$work/root-only"; runuser -u nobody -- env TRIM_APPDEST="$work/app/target" TRIM_PKGVAR="$work/app/var" \
        TRIM_TEMP_LOGFILE="$work/data/appcenter.log" "$control" "$@")
}
cleanup() {
    if [ "$started" = true ]; then app_control stop || true; fi
    rm -rf "$work"
}
trap cleanup EXIT
mkdir "$work/target" "$work/data" "$work/app" "$work/root-only"
ln -s "$work/target" "$work/app/target"
ln -s "$work/data" "$work/app/var"
cp -R "$publish_dir" "$work/target/bin"
cp -R "$web_dir" "$work/target/www"
chown -R nobody "$work"
chmod 755 "$work"
chown root "$work/root-only"
chmod 700 "$work/root-only"
started=true
app_control start || { cat "$work/data/server.log" "$work/data/appcenter.log"; exit 1; }
app_control status
first_pid=$(cat "$work/data/server.pid")
app_control start
[ "$(cat "$work/data/server.pid")" = "$first_pid" ]
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
app_control stop
status=0; app_control status || status=$?
[ "$status" = 3 ]
app_control stop
app_control start
[ "$(cat "$work/data/server.pid")" != "$first_pid" ]
app_control stop
started=false
mv "$work/target/bin/duplicate-cleanup" "$work/target/bin/duplicate-cleanup.saved"
if app_control start; then echo 'Missing binary unexpectedly started'; exit 1; fi
grep -q '后端程序不存在或不可执行' "$work/data/appcenter.log"
printf '#!/bin/sh\nexit 42\n' > "$work/target/bin/duplicate-cleanup"
chmod 755 "$work/target/bin/duplicate-cleanup"
if app_control start; then echo 'Failed binary unexpectedly started'; exit 1; fi
grep -q '退出码：42' "$work/data/appcenter.log"
grep -q '退出码：42' "$work/data/lifecycle.log"
echo 'PASS package lifecycle start, status, repeat start, stop, restart and startup failure reporting'
