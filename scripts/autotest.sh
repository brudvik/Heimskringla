#!/usr/bin/env bash
# Runs Heimskringla in autotest mode: flies through the world, fells a tree, mines rock,
# takes screenshots (shot_XX.png) and prints performance to the console.
#
# Usage:  scripts/autotest.sh <outdir> [seed]
# Environment variables that affect the test:
#   HK_TOOLROT="x,y,z"   override the rotation of the tool in the hand (degrees)
#   HK_NOGRASS=1         turn off the grass (debugging)
#   HK_NOSSAO=1          turn off SSAO (debugging)
#   HK_FROM=<n>          skip straight to step n (faster when only the last steps need checking)
#   HK_NOPRECIP=1        no rain/snow particles (debugging/performance)
#   HK_LOOKDOWN=1        step 7 takes the shot straight down at the ground (terrain shader check)
#   HK_DECALTEST=1       step 13 places a large red decal at the character (decal check)
#   HK_NOVOLFOG=1        turn off volumetric fog (performance/debugging)
#   HK_NOAUTOEXP=1       turn off auto exposure
#   HK_LANG=en|nb        force the game language
set -e
cd "$(dirname "$0")/.."
OUT="${1:?Specify output directory for screenshots}"
SEED="${2:-4242}"
mkdir -p "$OUT"
rm -f "$OUT"/shot_*.png
dotnet build -nologo -v q > /dev/null
GODOT="tools/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
# Import new/changed assets first (fast if nothing is new)
timeout 900 "$GODOT" --headless --import > /dev/null 2>&1 || true
timeout 300 "$GODOT" --path . -- --autotest="$OUT" --seed="$SEED" 2>&1 | grep -E "autotest|^FPS|Render|Draw|ERROR|Exception"
