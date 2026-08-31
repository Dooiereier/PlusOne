#!/usr/bin/env bash
#
# Builds the mod's transport and console code outside Unity and exercises it:
#
#   * protocol_test.py  - HTTP, WebSocket, MJPEG and JSON against the real classes
#   * console_test.py   - the baked web console driven in Chromium at tablet size
#
# Requirements: mono (mcs), python3, and for the console test the Python
# playwright package plus a Chromium build. Set CHROMIUM_PATH if Chromium is not
# at the default Playwright location.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
BUILD="${SECOND_SCREEN_BUILD:-$HERE/build}"
SOURCES="$ROOT/unity/Assets/JunoSecondScreen/Scripts"

rm -rf "$BUILD"
mkdir -p "$BUILD"

# Only the engine independent parts of the mod are compiled here; everything that
# touches ModApi or UnityEngine needs the real game assemblies and is exercised in
# game instead.
mapfile -t FILES < <(
  ls "$SOURCES"/Util/*.cs "$SOURCES"/Net/*.cs "$SOURCES"/Web/WebAssets.g.cs "$HERE"/harness/*.cs
)

echo "Building test harnesses..."
mcs -langversion:latest -target:exe -main:JunoSecondScreen.Tests.Protocol.ProtocolHarness \
    -out:"$BUILD/protocol-harness.exe" "${FILES[@]}"
mcs -langversion:latest -target:exe -main:JunoSecondScreen.Tests.Console.ConsoleHarness \
    -out:"$BUILD/console-harness.exe" "${FILES[@]}"

export SECOND_SCREEN_BUILD="$BUILD"

echo
echo "== Transport tests =="
python3 "$HERE/protocol_test.py"

echo
echo "== Console tests =="
if python3 -c "import playwright" 2>/dev/null; then
  python3 "$HERE/console_test.py"
else
  echo "SKIP: python playwright is not installed (pip install playwright)"
fi
