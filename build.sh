#!/bin/sh

set -eu

PROJECT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$PROJECT_DIR"

SHORT_HASH=$(git rev-parse --short=7 HEAD)
OUTPUT_ZIP="Surfing_windows-amd64_${SHORT_HASH}.zip"
STAGING_DIR=$(mktemp -d)

trap 'rm -rf "$STAGING_DIR"' 0
trap 'exit 1' HUP INT TERM

# Export committed release inputs only; never copy local subscriptions or caches.
git archive --format=zip --output="$STAGING_DIR/$OUTPUT_ZIP" HEAD -- \
    README.md LICENSE \
    bin/clash-amd64.exe bin/SurfingTray.exe \
    src/tray/TrayCore.cs src/tray/TrayApplication.cs src/tray/FluentControlCenter.cs src/tray/SurfingTray.manifest src/tray/SurfingTray.ico \
    config/config.yaml config/rules config/webroot \
    'scripts/*.vbs' 'scripts/*.bat' 'scripts/*.ps1' \
    scripts/Surfing.startup.xml \
    启动服务.bat 停止服务.bat 开机自启.bat 关闭自启.bat 托盘管理.bat

mv -f "$STAGING_DIR/$OUTPUT_ZIP" "$PROJECT_DIR/$OUTPUT_ZIP"
printf 'Created %s from committed HEAD (%s)\n' "$OUTPUT_ZIP" "$SHORT_HASH"
