# Heimskringla — project rules

## Language
This is an open-source project. **Everything in the repository is written in English:**
- Commit messages (subject and body), tags, release notes, PR titles/descriptions
- Code comments, XML doc comments, shader comments, script comments and docstrings
- Documentation (README, CONTRIBUTING, docs/), script usage/help text and developer log output
- Identifiers (classes, methods, variables, files)

The only exception is **player-facing text**, which is localized: never hard-code UI strings in code —
add a key to the translation files in `translations/` with both English (`en`, the default/fallback) and
Norwegian Bokmål (`nb`) text. The game follows the system language and falls back to English.

The developer may chat in Norwegian; reply in Norwegian, but still write all repository content in English.

## Building and testing
- `run.cmd [seed]` builds and runs; `editor.cmd` opens the editor. Godot 4.7.2 .NET lives in `tools/` (git-ignored).
- `scripts/autotest.sh <outdir> [seed]` runs a scripted fly-through, saves screenshots and logs performance.
  It must never capture the mouse or steal focus (`Game.AutoTest` guards this).
- Reusable helper scripts belong in `scripts/`.

## Releasing
Builds and releases are made locally — **no GitHub Actions or other paid CI.**

`scripts/release.sh [X.Y.Z] --publish --notes <notes.md>` exports the Windows build, zips it as
`build/heimskringla-YYYY-MM-DD-X.Y.Z.zip`, tags `vX.Y.Z` and creates the GitHub release.
