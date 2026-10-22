# Gnosia Translation Patcher

A fan-made, one-click translation patcher for the PC (Steam) version of **[Gnosia](https://store.steampowered.com/app/1608290/GNOSIA/)**,
the sci-fi social deduction RPG by Petit Depotto.

The game ships in **English, Japanese and Simplified Chinese**. This patcher adds:

| Language | Status |
|---|---|
| 🇧🇷 Português (Brasil) | ✅ complete, including images |
| 🇪🇸 Español | ✅ complete, including images |
| 🇩🇪 Deutsch | ✅ complete, including images |
| 🇫🇷 Français | ✅ complete, including images |
| 🇷🇺 Русский | ✅ complete, including images |

> 🇧🇷 **Português:** baixe o `GnosiaTranslationPatcher.exe` em *Releases*, feche o jogo, rode o programa e escolha
> "Português (Brasil)". Depois, nas opções do jogo, deixe o idioma em **English**. Para desfazer, rode de novo e
> escolha "Restore".

## How to use

1. Download `GnosiaTranslationPatcher.exe` from **Releases**.
2. Close the game and run it. It finds the game in your Steam libraries (or asks for the `Gnosia_Data` folder).
3. Pick a language.
4. In the game's options, set the language to **English** — the English text is what gets replaced.

Run it again and choose **Restore** to undo. A Steam update or "Verify integrity of game files" also restores the original.

Command line: `GnosiaTranslationPatcher.exe --lang pt-BR` · `--restore` · `--game "<path>\Gnosia_Data"`.

## How it works

Gnosia is a Unity 2019.4 (Mono) game. All dialogue lives in ~20 `ScriptableObject` tables
(`Entity_CharaText`, `Entity_ScenarioSetsuText`, …) inside `sharedassets0.assets`; every row holds the same line in
Japanese, English and Chinese.

```
                 ┌──────────────────────────────┐
 sharedassets0 ──▶  AssetsTools.NET + Mono.Cecil │  type trees rebuilt from Assembly-CSharp.dll
   .assets       │  (file ships without them)    │  and Unity's class database
                 └──────────────┬───────────────┘
                                ▼
          text rows ── key "Asset/sheet:name/row" ──▶ English column ← translation
          TMP fonts ── SDF glyph compositing      ──▶ á ã ç ñ ü ¿ ¡ … + Cyrillic fallback
                                ▼
                 backup  +  rewritten sharedassets0.assets
```

**Text.** Each row is addressed by a stable key (`ScenarioSetsuText/3:Setsu4/17`), so translation files contain only
the translated text — never the game's own script. Rows whose `{0}` placeholders would be lost are skipped.

**Fonts.** The Latin TextMeshPro fonts only contain ASCII, so `é` would render as a blank square. Instead of
re-rasterizing a font, [`FontPatch.cs`](src/GnosiaPatcher/FontPatch.cs) builds every missing letter out of glyphs
already in the atlas: base letter + an ASCII mark (`` ` ^ ~ , . ``), scaled, mirrored (acute = mirrored grave),
doubled (diaeresis) or rotated 180° (`¿ ¡`). Signed distance fields combine with `max()`, so the result has the
same smooth outline the shader expects. The atlas is doubled in height to hold the new glyphs. Cyrillic is
provided by pointing the Latin fonts' fallback list at the game's Japanese font, which already contains it.

**Safety.** The original file is backed up once (`sharedassets0.assets.original`) and every patch is built from that
backup, so switching languages never stacks changes. The original's MD5 is checked to warn about other game versions.

## Repository layout

```
src/GnosiaPatcher/   C# patcher (.NET 8, single-file self-contained exe)
translations/        <lang>.tsv — "key<TAB>text", embedded into the exe
tools/               scripts used to produce translations (see docs/TRANSLATING.md)
docs/TRANSLATING.md  how to translate / add a language
third_party/         Unity class database used by AssetsTools.NET
```

## Building

```
dotnet publish src/GnosiaPatcher -c Release -o publish
```

## Adding or improving a translation

See [docs/TRANSLATING.md](docs/TRANSLATING.md). The patcher can export the source text from *your own copy* of the
game (`--export`) and validate a translation (`--check`).

**Images.** Menus, buttons, the title screen, the Setup screen and character creation have their English text painted
into textures. [`translations/images.json`](translations/images.json) lists the box of each text; at install time
[`ImagePatch.cs`](src/GnosiaPatcher/ImagePatch.cs) rebuilds the background under it, measures the English text
(size, baseline, color, alignment) and draws the translation in the same place with the game's own SDF fonts. The
modified Addressables bundles are backed up, re-packed with LZ4, and the catalog's bundle CRCs are disabled so the
game accepts them. Image texts are translated like any other line (`Image/<id>` keys).

## Not translated

The in-game Help pages and some smaller screens (results, voting, gallery) are still images in English, as is the
controller-mapping screen.

## Legal

Unofficial fan project, not affiliated with Petit Depotto or PLAYISM. Gnosia and its content are © Petit Depotto /
PLAYISM. This repository contains no game files or original script (only the short English labels
`translations/images.json` needs to find text inside images); the patcher modifies the copy you own.
Code is MIT-licensed (see [LICENSE](LICENSE)); third-party components are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
