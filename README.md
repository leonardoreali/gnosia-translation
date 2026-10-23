# Gnosia Translation Patcher

A fan-made, one-click translation patcher for the PC (Steam) version of **[Gnosia](https://store.steampowered.com/app/1608290/GNOSIA/)**,
the sci-fi social deduction RPG by Petit Depotto.

The game ships in **English, Japanese and Simplified Chinese**. This patcher adds:

| Language | Translated | Tested in game | Reviewed by a native speaker |
|---|---|---|---|
| 🇧🇷 Português (Brasil) | ✅ text + images | ✅ | ✅ |
| 🇪🇸 Español | ✅ text + images | ✅ | ❌ |
| 🇩🇪 Deutsch | ✅ text + images | ❌ | ❌ |
| 🇫🇷 Français | ✅ text + images | ❌ | ❌ |
| 🇷🇺 Русский | ✅ text + images | ❌ | ❌ |

> **Made with AI.** I built this project with a lot of help from AI (Claude, by Anthropic): it wrote most of the code
> and the first draft of every translation, while I'm still learning programming. Only Brazilian Portuguese has been
> reviewed by a native speaker so far; the other languages may have mistakes or awkward phrasing.
> Native speakers who want to help are very welcome — see [Contributing](#contributing).

### Other fan translations

Other fans have put a lot of work into hand-made translations. Please check them out:

- 🇫🇷 **Français:** a complete, human-made translation by [Équipe Mirai](https://equipemirai.com/gnosia/) and
  [Dream Team Trad](https://www.dreamteamtrad.fr/gnosia/) (2024). **If you play in French, we recommend theirs.**
- 🇷🇺 **Русский:** a full translation by QuestLion, ZedcliF and Libert, announced in December 2025
  ([news](https://www.playground.ru/gnosia/news/gnosia_poluchit_polnotsennyj_fanatskij_russkij_perevod-1811855)).
  Check its current status before choosing this patcher's Russian.

Know another one? Open an issue and it will be added here.

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

**Words in the code.** The Doctor's and Engineer's reports (`{1} {3} {0}.`) get `{3}`–`{5}` filled by the game's
code with the English "was / were / wasn't / weren't". [`CodePatch.cs`](src/GnosiaPatcher/CodePatch.cs) replaces
those few string constants in `Assembly-CSharp.dll` (backed up as `Assembly-CSharp.dll.original`) with the
translation's `Code/was`, `Code/wasn't`, `Code/were` and `Code/weren't` entries.

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

## Contributing

Anyone can help, no programming needed:

- **Found a wrong or awkward line?** Open an [issue](../../issues) with the language, a screenshot or the line as it
  appears in the game, and your suggested fix.
- **Want to review or fix a language?** Translations are plain text files, `translations/<lang>.tsv`, one line per
  `key<TAB>text`. Edit the file on GitHub (pencil icon) or in a fork and open a pull request. Keep `{0}`, `\n` and
  the key on the left exactly as they are.
- **Want to add a language or do bigger work?** See [docs/TRANSLATING.md](docs/TRANSLATING.md). The patcher can export
  the source text from *your own copy* of the game (`--export`) and validate a translation (`--check`).

Reviewers are credited in the README and the release notes.

**Images.** Menus, buttons, the title screen, the Setup screen and character creation have their English text painted
into textures. [`translations/images.json`](translations/images.json) lists the box of each text; at install time
[`ImagePatch.cs`](src/GnosiaPatcher/ImagePatch.cs) rebuilds the background under it, measures the English text
(size, baseline, color, alignment) and draws the translation in the same place with the game's own SDF fonts. The
modified Addressables bundles are backed up, re-packed with LZ4, and the catalog's bundle CRCs are disabled so the
game accepts them. Image texts are translated like any other line (`Image/<id>` keys).

## Credits

- [leonardoreali](https://github.com/leonardoreali): project, Brazilian Portuguese review
- [AndersonRx](https://github.com/AndersonRx): playtesting
- Built with AI assistance (Claude, by Anthropic)

## Not translated

Still images in English: the in-game Help pages, the controller-mapping screen, the Credits screen (mostly names,
drawn over the artwork), the small NAME / ROLE / TARGET
labels of the dialogue window, "Usage" and the vertical "Req'd" in the Command List, and a few labels of the
results and gallery screens. The headers of the Command List, Current Status, Crew Data, Discussion Log, the
Doctor/Engineer reports, voting and the ability graph are translated.

## Legal

Unofficial fan project, not affiliated with Petit Depotto or PLAYISM. Gnosia and its content are © Petit Depotto /
PLAYISM. This repository contains no game files or original script (only the short English labels
`translations/images.json` needs to find text inside images); the patcher modifies the copy you own.
Code is MIT-licensed (see [LICENSE](LICENSE)); third-party components are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
