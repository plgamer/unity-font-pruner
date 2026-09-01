# Font Pruner

*[中文文档](README.zh-CN.md)*

A Unity editor window that subsets TTF fonts down to just the characters your
game actually ships. A CJK font drops from tens of megabytes to tens of
kilobytes when you only need the few hundred glyphs that appear in your UI.

Menu: **Tools → 字体精简工具 (FontPruner)**

## Why another one

Two things this does that a plain `sfnttool` command line doesn't:

- **It reads your Localization tables.** Point it at your
  `com.unity.localization` string tables, pick the collections and locales you
  ship, and it collects the character set for you. No maintaining a charset
  file by hand that silently drifts out of date.
- **It tells you what's missing before it runs.** It parses the source font's
  `cmap` directly, so you see exactly which requested characters that font
  doesn't contain — instead of discovering the tofu boxes after the build.

It also cleans up after `sfnttool`, which leaves orphaned `vhea` / `VORG` /
`BASE` tables behind. Those make macOS Font Book reject the output with
`hmtx` / `vmtx` availability errors.

## Requirements

| | |
|---|---|
| Unity | 2022.3 (the only version this is verified on; it does not use any newer API, so earlier LTS releases will likely work) |
| Java | A JRE or JDK on `PATH`, in `JAVA_HOME`, or pointed at manually in the window. Verified on Temurin 11 |
| Packages | `com.unity.textmeshpro`, `com.unity.localization` — both are declared as dependencies and installed automatically via UPM |

Input fonts must be **TrueType-outline `.ttf`**. CFF/OpenType (`.otf`) outlines
are not supported by the underlying sfnttool.

## Install

### Via UPM (recommended)

Window → Package Manager → **+** → *Add package from git URL*:

```
https://github.com/plgamer/unity-font-pruner.git
```

Or add it to `Packages/manifest.json` directly:

```json
"com.plgamer.fontpruner": "https://github.com/plgamer/unity-font-pruner.git"
```

### By hand

Download the repository and copy the `Editor/` folder into your project as
`Assets/Editor/FontPruner/`. Keep `Tools~/` — the trailing `~` is what stops
Unity from importing the 10 MB jar as an asset.

Don't do both. Two copies of the same classes in one project will not compile.

## Using it

The window walks top to bottom.

**① Characters to keep.** Type them in, or use the presets (digits, upper,
lower, ASCII punctuation, all printable ASCII). *Collect from localization
tables* pulls every character out of the string table collections and locales
you tick. Dedupe, import from txt, and export to txt are all there.

**② Source fonts.** Select `.ttf` assets in the Project window and hit *Add
from selection*. Each font expands to a coverage report against the character
set from ①.

**③ Output.** Four modes:

| Mode | Behavior |
|---|---|
| Separate folder | Writes to a folder under the project root. Leaves `Assets` untouched — the safe default |
| Same folder + suffix | Writes next to the source as `MyFont-pruned.ttf` |
| Overwrite source | Replaces the font in place. The original is backed up to the project root first |
| `_Origin` master → target | Treats `MyFont_Origin.ttf` as the untouched master and overwrites `MyFont.ttf` beside it. Lets you re-prune from the full font whenever the character set grows |

That last mode is the one worth setting up if the tool becomes part of your
workflow: keep the full font as `X_Origin.ttf`, and re-running the prune is
always lossless because it never consumes its own output.

**④ Environment.** Java path — auto-detected from `JAVA_HOME`, `PATH`, then
`/usr/bin/java`, with a manual override if you need a specific JDK.

Then run. Results list per-font before/after sizes, and there's a button to
open the output folder.

## Settings

Persisted to `ProjectSettings/FontPrunerSettings.json`, not under `Assets`.
Commit it and the whole team shares one character set and output configuration.

## License

MIT — see [LICENSE](LICENSE).

The bundled `sfnttool.jar` is third-party and carries its own licenses
(Apache-2.0, Unicode/ICU, EPL-1.0, BSD-3-Clause). See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
