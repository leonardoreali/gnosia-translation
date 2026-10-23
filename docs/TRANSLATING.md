# Translating Gnosia

Gnosia ships with three text columns: Japanese, English and Simplified Chinese.
The patcher writes a translation **into the English column**, so players pick
"English" in the game's options to see it. This guide explains how a translation is made.

## 1. Get the source text (from your own copy of the game)

The original script is copyrighted, so it is **not** in this repository. Generate it locally:

```
GnosiaTranslationPatcher.exe --export _work\reference.tsv
powershell -File tools\make-unique.ps1
```

- `_work/reference.tsv`: every text row as `key <TAB> English <TAB> Japanese <TAB> Chinese`.
- `_work/unique.tsv`: the same English text de-duplicated (~7,800 lines) as `id <TAB> asset:sheet <TAB> English`.
  Translate this one: repeated lines are translated once.

`_work/` is in `.gitignore`. Never commit it.

## 2. Translate

Write `_work/<lang>/*.tsv` files with one line per unique id:

```
id|translated text
```

Split the work into as many files as you like (e.g. `0001.tsv`, `0301.tsv`…); they are merged by id.

### Rules

| Keep exactly | Why |
|---|---|
| `{0}`, `{1}`… | Names and roles inserted by the game. Every placeholder of the English line must appear. |
| `\n` (backslash + n) | Line break, stored literally by the game. Keep lines about as long as the English ones; text boxes do not wrap well. |
| `\"` | An escaped quote, as in the source. Plain `"` also works. |
| Leading/trailing spaces | Some lines are fragments glued together by the game (`" was Gnosia"`, `"Last night, "`). Translate them so they still join. |

**Doctor and Engineer reports.** In lines like `{1} {3} {0}.` the game itself fills `{3}`/`{4}` with "was" or
"were" (plural when several people are named) and `{5}` with "wasn't" or "weren't". Give the words for your
language in four extra lines of `translations/<lang>.tsv`, and place `{3}` where that word goes in your sentence
(e.g. Portuguese `{1} não {3} {0}.` → "Stella não era Gnosia"):

```
Code/was	era
Code/wasn't	não era
Code/were	eram
Code/weren't	não eram
```

- Lines that are only names, symbols or internal labels can be omitted; the English stays.
- Context column (`asset:sheet`) tells who speaks: `Chara:Setsu` are Setsu's lines, `ScenarioSQ:SQ3` is an SQ event.
- **Setsu and Raqio are non-binary.** Avoid gendered forms for them where the language allows.
  The player's gender is unknown: avoid gendered forms for `{0}` when it is the player, or use the
  language's usual inclusive notation.
- Keep each character's voice: SQ's valley-girl filler, Jonas's archaic bombast, Remnan's hesitation,
  Sha-Ming's crudeness, Kukrushka's narrated gestures, Raqio's arrogance.
- Keep a glossary for recurring game terms (roles, skills, "cold sleep", room names) and use it consistently.

### Characters

The game's Latin fonts only had ASCII. The patcher adds
`àèìòùÀÈÙ áéíóúýÁÉÍÓÚÝ âêîôûÂÊÎÔÛ ãõñÃÕÑ äëïöüÿÄËÏÖÜ çÇ ¿¡`.
Cyrillic is drawn by the game's Japanese font. These are converted automatically:
`ß→ss`, `œ→oe`, `æ→ae`, `«» „ “ ”→"`, `’→'`, `— –→-`.
Anything else may show as a blank square: `--check` reports it.

## 3. Build and check

```
powershell -File tools\ids-to-keys.ps1 -Lang <lang> -Source _work\<lang>
GnosiaTranslationPatcher.exe --check translations\<lang>.tsv --ref _work\reference.tsv
```

`ids-to-keys` writes `translations/<lang>.tsv` (`key <TAB> text`), which is what the patcher embeds.
`--check` lists missing placeholders and characters the fonts cannot draw.

To try it in the game without rebuilding the patcher:

```
GnosiaTranslationPatcher.exe --lang <lang> --file translations\<lang>.tsv
```

To ship a new language, add it to `Language.Patchable` in `src/GnosiaPatcher/Languages.cs`.
