#!/usr/bin/env bash
#
# Type checks the whole mod - including everything that talks to ModApi - without
# opening Unity, by compiling against the game's own assemblies from the Mod Tools
# plus small stand-ins for the Unity engine modules.
#
# Point MOD_TOOLS_ASSEMBLIES at the Assemblies folder of an imported Mod Tools
# package, for example:
#
#   MOD_TOOLS_ASSEMBLIES=~/JunoMod/Assets/ModTools/Assemblies ./tests/typecheck.sh
#
# Requires mono (mcs).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
BUILD="${SECOND_SCREEN_BUILD:-$HERE/build}"
ASSEMBLIES="${MOD_TOOLS_ASSEMBLIES:-}"

if [[ -z "$ASSEMBLIES" || ! -f "$ASSEMBLIES/ModApi.dll" ]]; then
  echo "Set MOD_TOOLS_ASSEMBLIES to the Mod Tools 'Assemblies' folder (the one containing ModApi.dll)." >&2
  exit 2
fi

mkdir -p "$BUILD"

echo "Building Unity stand-ins..."
mcs -langversion:latest -target:library -out:"$BUILD/Unity.Collections.dll" "$HERE/stubs/Unity.Collections.cs"
mcs -langversion:latest -target:library -out:"$BUILD/UnityEngine.CoreModule.dll" \
    -r:"$BUILD/Unity.Collections.dll" "$HERE/stubs/UnityEngine.cs"

echo "Type checking the mod..."
mapfile -t SOURCES < <(find "$ROOT/unity/Assets/JunoSecondScreen/Scripts" -name '*.cs')
mcs -langversion:latest -target:library -out:"$BUILD/JunoSecondScreen.dll" \
    -r:"$BUILD/UnityEngine.CoreModule.dll" \
    -r:"$BUILD/Unity.Collections.dll" \
    -r:"$ASSEMBLIES/ModApi.dll" \
    -r:"$ASSEMBLIES/ModApi.Core.dll" \
    -r:"$ASSEMBLIES/Jundroo.ModTools.dll" \
    -r:"$ASSEMBLIES/Jundroo.Packages.dll" \
    "${SOURCES[@]}"

echo "OK: the mod compiles against ModApi."
