#!/usr/bin/env bash
# Kjører Heimskringla i autotest-modus: flyr gjennom verden, feller et tre, hakker stein,
# tar skjermbilder (shot_XX.png) og skriver ytelse til konsollen.
#
# Bruk:  scripts/autotest.sh <utmappe> [seed]
# Miljøvariabler som påvirker testen:
#   HK_TOOLROT="x,y,z"   overstyr rotasjonen av verktøyet i hånden (grader)
#   HK_NOGRASS=1         slå av gresset (feilsøking)
#   HK_NOSSAO=1          slå av SSAO (feilsøking)
#   HK_FROM=<n>          hopp rett til steg n (raskere når bare de siste stegene skal sjekkes)
#   HK_NOPRECIP=1        ingen regn/snø-partikler (feilsøking/ytelse)
#   HK_LOOKDOWN=1        steg 7 tar bildet rett ned på bakken (sjekk av terrengshader)
#   HK_DECALTEST=1       steg 13 legger en stor rød decal ved figuren (sjekk av decals)
set -e
cd "$(dirname "$0")/.."
OUT="${1:?Oppgi utmappe for skjermbilder}"
SEED="${2:-4242}"
mkdir -p "$OUT"
rm -f "$OUT"/shot_*.png
dotnet build -nologo -v q > /dev/null
GODOT="tools/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
# Importer nye/endrede assets først (rask hvis ingenting er nytt)
timeout 900 "$GODOT" --headless --import > /dev/null 2>&1 || true
timeout 300 "$GODOT" --path . -- --autotest="$OUT" --seed="$SEED" 2>&1 | grep -E "autotest|^FPS|Render|Draw|ERROR|Exception"
