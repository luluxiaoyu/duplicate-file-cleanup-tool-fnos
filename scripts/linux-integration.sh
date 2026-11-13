#!/usr/bin/env bash
set -euo pipefail
# CI setup may use root to create disposable loop filesystems. The tested application code never
# runs as root and never changes machine/system disks.
test_binary=${1:?Pass the published AOT tests directory}
test_binary=$(cd "$test_binary" && pwd)
work=$(mktemp -d /tmp/duplicate-linux-tests.XXXXXX)
chmod 755 "$work"
cleanup() { umount "$work/mount" 2>/dev/null || true; rm -rf "$work"; }
trap cleanup EXIT
mkdir "$work/mount"
for fs in ext4 btrfs; do
    truncate -s 1G "$work/$fs.img"
    if [ "$fs" = ext4 ]; then mkfs.ext4 -q -F "$work/$fs.img"; else mkfs.btrfs -q -f "$work/$fs.img"; fi
    mount -o loop "$work/$fs.img" "$work/mount"
    chown nobody "$work/mount"
    runuser -u nobody -- env DUPLICATE_TEST_ROOT="$work/mount" "$test_binary/Tests"
    umount "$work/mount"
done
