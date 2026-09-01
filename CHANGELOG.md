# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-09-01

### Added

- Initial public release, extracted from a private Unity project.
- Character set editor: manual input, ASCII presets (digits / upper / lower /
  punctuation / all printable ASCII), dedupe, txt import and export.
- Character collection from Unity Localization string tables, filterable by
  table collection and by locale.
- Font list built from the current Project selection, with per-font coverage
  preview that reads the source `cmap` so you know which characters are missing
  *before* you run.
- Four output modes: separate folder, same folder with suffix, overwrite source
  (with automatic backup), and `X_Origin.ttf` master to `X.ttf` target.
- Orphan table cleanup (`vhea` / `VORG` / `BASE`) so macOS Font Book stops
  reporting `hmtx` / `vmtx` availability errors on pruned output.
- Java auto-detection with a manual override.
- Settings persisted to `ProjectSettings/FontPrunerSettings.json`, shared via
  version control rather than stored under `Assets`.
