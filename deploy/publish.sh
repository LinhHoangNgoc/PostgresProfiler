#!/usr/bin/env bash
# Tạo bản build self-contained cho Linux & Windows, kèm script setup.
# Yêu cầu: .NET 8 SDK. Chạy từ thư mục gốc project.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

build() {
  local rid="$1" out="dist/pgmonitor-$1"
  echo "=== Publish $rid -> $out ==="
  rm -rf "$out"
  dotnet publish -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$out"
  # Kèm script setup tương ứng
  case "$rid" in
    linux-*) cp deploy/setup.sh "$out/" && chmod +x "$out/setup.sh" ;;
    win-*)   cp deploy/setup.ps1 "$out/" ;;
  esac
  cp deploy/README.md "$out/HUONG-DAN.md" 2>/dev/null || true
  echo "    -> $out"
}

build linux-x64
build win-x64
echo "Xong. Bản build nằm trong thư mục dist/"
