#!/usr/bin/env bash
# Lager en Windows-release av Heimskringla og (valgfritt) publiserer den på GitHub.
#
# Bruk:  scripts/release.sh [versjon] [--publish] [--notes <fil>]
#   versjon     f.eks. 0.2.0. Utelatt: øker patch-nummeret fra siste v*-tag (0.1.0 -> 0.1.1).
#   --publish   committer versjonsnummeret, tagger vX.Y.Z, pusher og lager GitHub-release med zip-en.
#   --notes     markdown-fil med release-notater. Utelatt: commit-listen siden forrige tag
#               pluss en fast "Download & run"-del.
#
# Resultat: build/heimskringla-YYYY-MM-DD-X.Y.Z.zip (mappa Heimskringla/ med exe, .NET-runtime og lisenser).
# Krever: Godot 4.7.2 mono i tools/, eksportmaler 4.7.2.stable.mono, python, gh (for --publish).
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=""; PUBLISH=0; NOTES=""
while [ $# -gt 0 ]; do
  case "$1" in
    --publish) PUBLISH=1 ;;
    --notes) NOTES="$2"; shift ;;
    *) VERSION="$1" ;;
  esac
  shift
done

PREV_TAG="$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || true)"
if [ -z "$VERSION" ]; then
  if [ -n "$PREV_TAG" ]; then
    IFS=. read -r MA MI PA <<< "${PREV_TAG#v}"
    VERSION="$MA.$MI.$((PA + 1))"
  else
    VERSION="0.1.0"
  fi
fi
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Ugyldig versjon: $VERSION (forventer X.Y.Z)"; exit 1; }
TAG="v$VERSION"
DATE="$(date +%Y-%m-%d)"
ZIP="build/heimskringla-$DATE-$VERSION.zip"

if [ "$PUBLISH" = 1 ]; then
  [ -z "$(git status --porcelain)" ] || { echo "Arbeidskopien er ikke ren - commit først."; exit 1; }
  git rev-parse -q --verify "refs/tags/$TAG" > /dev/null && { echo "Taggen $TAG finnes allerede."; exit 1; }
fi

echo "== Heimskringla $VERSION ($DATE)"

# Versjonsnummer i exe-ens filinfo
sed -i -E "s/^application\/(file|product)_version=\".*\"/application\/\1_version=\"$VERSION.0\"/" export_presets.cfg

GODOT="tools/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
OUT="build/windows"
rm -rf "$OUT" && mkdir -p "$OUT"

echo "== Bygger C#"
dotnet build -nologo -v q -c Release > /dev/null

echo "== Eksporterer (kan ta 10-15 min)"
timeout 1800 "$GODOT" --headless --path . --export-release "Windows Desktop" "$OUT/Heimskringla.exe" > build/export.log 2>&1 || true
if grep -q "^ERROR" build/export.log || [ ! -f "$OUT/Heimskringla.exe" ] || [ ! -f "$OUT"/data_Heimskringla_windows_x86_64/Heimskringla.dll ]; then
  echo "Eksporten feilet - se build/export.log"; grep "^ERROR" build/export.log | sort | uniq | head; exit 1
fi

cp assets/LICENSES.md "$OUT/THIRD-PARTY-LICENSES.md"
cp assets/fonts/OFL-Cinzel.txt "$OUT/"

echo "== Pakker $ZIP"
rm -f "$ZIP"
python - "$OUT" "$ZIP" <<'PY'
import os, sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for root, _, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.join("Heimskringla", os.path.relpath(p, src)))
PY
ls -la "$ZIP"

if [ "$PUBLISH" != 1 ]; then
  git checkout -q -- export_presets.cfg   # versjonsnummeret committes bare ved --publish
  echo "Ferdig (ikke publisert). Kjør med --publish for å legge ut på GitHub."; exit 0
fi

echo "== Publiserer $TAG"
if [ -n "$(git status --porcelain export_presets.cfg)" ]; then
  git add export_presets.cfg
  git commit -q -m "Versjon $VERSION" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
fi
git push -q origin HEAD
git tag -a "$TAG" -m "Heimskringla $VERSION"
git push -q origin "$TAG"

if [ -z "$NOTES" ]; then
  NOTES="build/release-notes.md"
  {
    echo "## Changes"
    if [ -n "$PREV_TAG" ]; then git log --no-merges --format='- %s' "$PREV_TAG..$TAG" | grep -v "^- Versjon $VERSION$" || true
    else git log --no-merges --format='- %s' "$TAG"; fi
    echo
    echo "## Download & run"
    echo "1. Download \`$(basename "$ZIP")\` and unzip it anywhere."
    echo "2. Run \`Heimskringla/Heimskringla.exe\`."
    echo
    echo "Requires Windows 10/11 (x64) and a reasonably modern GPU. No .NET install needed. The build is not code-signed, so SmartScreen may warn on first launch (\"More info\" → \"Run anyway\"). Press **F1** in-game for controls."
  } > "$NOTES"
fi

gh release create "$TAG" "$ZIP" --title "Heimskringla $VERSION ($DATE)" --notes-file "$NOTES"
