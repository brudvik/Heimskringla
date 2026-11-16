#!/usr/bin/env bash
# Kjører Heimskringla i autotest-modus: flyr gjennom verden, feller et tre, hakker stein,
# tar skjermbilder (shot_XX.png) og skriver ytelse til konsollen.
#
# Bruk:  scripts/autotest.sh <utmappe> [seed]
# Miljøvariabler som påvirker testen:
#   HK_TOOLROT="x,y,z"   overstyr rotasjonen av verktøyet i hånden (grader)
set -e
cd "$(dirname "$0")/.."
OUT="${1:?Oppgi utmappe for skjermbilder}"
SEED="${2:-4242}"
mkdir -p "$OUT"
rm -f "$OUT"/shot_*.png
dotnet build -nologo -v q > /dev/null
GODOT="tools/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
timeout 300 "$GODOT" --path . -- --autotest="$OUT" --seed="$SEED" 2>&1 | grep -E "autotest|^FPS|Render|Draw|ERROR|Exception"
