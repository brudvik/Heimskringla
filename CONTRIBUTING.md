# Contributing to Heimskringla

Thanks for your interest! A few ground rules:

## Language
- **Write everything in English:** commit messages, code comments, documentation, identifiers,
  log output, issues and pull requests.
- **Player-facing text is localized.** Don't hard-code UI strings; add a key to the CSV files in
  `translations/` with English (`en`) and Norwegian Bokmål (`nb`) text. English is the fallback.

## Commits
- Short imperative subject line (e.g. "Add birch bark shader"), optionally followed by a blank line
  and a bulleted body explaining what and why.
- Keep unrelated changes in separate commits.

## Building and testing
- Install the .NET 8 SDK and Godot 4.7.2 .NET, unpacked to `tools/Godot_v4.7.2-stable_mono_win64/`.
- `run.cmd [seed]` builds and runs the game; `editor.cmd` opens the Godot editor.
- `scripts/autotest.sh <outdir> [seed]` runs a scripted fly-through and saves screenshots — please
  run it and look at the screenshots before submitting visual changes.

## Assets
Only add third-party assets with a license that allows redistribution (preferably CC0), and list
them in `assets/LICENSES.md`.
