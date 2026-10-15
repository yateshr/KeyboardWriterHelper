# Keyboard Writer Helper

A small, high-performance Windows tray app that fixes spelling mistakes in any program as you type.
When you press Space after a misspelled word, it is replaced with the most likely
intended word (`improrvie ` becomes `improve `). Nothing you type is sent anywhere,
and nothing is saved unless you ask for it (see Typos below).

The rule behind every fix: changing correct text is worse than missing a mistake.
When the app is not sure, it leaves the word as typed.

## Build and run

Two ways, both without extra packages:

- **.NET 10 SDK**:
  ```powershell
  dotnet build
  dotnet run
  ```
- **The C# compiler that ships with Windows** (.NET Framework 4.x, no SDK needed):
  ```cmd
  build.cmd
  KeyboardWriterHelper.exe
  ```

A green "Aa" icon appears in the system tray. Exit the app from its tray menu
before building again: Windows locks the exe while it runs.

## Features & Use

- **Instant Correction & Undo**:
  - Press **Backspace** right after a fix to undo it (the word is then left as typed).
  - Undo the same fix twice and the word is added to *My words* automatically.
- **Candidate Switching & Floating Candidate Pill**:
  - **Ctrl+Alt+N** right after a fix swaps in the next-best word (`ther ` gives `there`;
    the shortcut then offers `their`, `the`, `they`, and last what you typed).
  - A floating pill near the caret shows the alternatives and the one in use, without taking the focus.
- **Multi-Word Phrase Grammar Correction**:
  - Catches 2-, 3- and 4-word slips: `their is` → `there is`, `could of been` → `could have been`,
    `one in the same` → `one and the same`, `all of the sudden` → `all of a sudden`.
  - Agreement and article slips where the sentence makes them certain: `he have a car` → `he has a car`,
    `could went` → `could go`, `a apple` → `an apple`. Correct English such as `Does it have`,
    `If he were`, `Is there going to be`, `The can fell` or `Plan A` is left alone
    (see `tests/must-not-change.txt`).
- **Smart Compose (off by default)**:
  - Guesses the next word from the words before (`thank ` → `you`, `look forward ` → `to`).
  - **Tab** accepts the guess only while the pill shows it; at any other moment Tab belongs to
    the program, so moving between form fields or Excel cells works as usual.
  - Backspace right after accepting takes the guess back.
- **Smart Typography (off by default)**:
  - Smart dashes: `-- ` and `--- ` become em-dash `— `.
  - Smart ellipsis: `... ` becomes `… `.
  - Smart arrows: `-> ` becomes `→ `, `=> ` becomes `⇒ `, `<- ` becomes `← `.
- **Dynamic Text Snippets & Macros** in *My fixes*:
  - `{date}` or `:date:` → the date (`2026-01-31`).
  - `{time}` or `:time:` → the time (`14:05`).
  - `{datetime}` or `:datetime:` → `2026-01-31 14:05`.
  - `{clip}` or `:clip:` → the clipboard text, on one line (line breaks become spaces, so a
    snippet never sends a chat message early).
  - Example: `now={time}`, `today={date}`, `myclip={clip}`.
- **Keyboard Layout Proximity**:
  - "Nearby key" slips are scored on the layout you type with. *Auto* reads the letters off the
    active keyboard layout's keys, so QWERTY, QWERTZ, AZERTY, Dvorak, Colemak and other Latin
    layouts all work; a layout can also be forced in Settings.
  - `KeyboardWriterHelper.exe --layout` shows what was read.
- **Skipped automatically**:
  - Fullscreen games and other borderless fullscreen windows (maximized normal windows are not skipped).
  - Password boxes in Windows programs (edit controls that hide their text). Browsers and WPF
    programs do not report password boxes, so add password-heavy programs to *Programs* if needed.
  - Terminals, password managers and remote-desktop programs (the default *Programs* list).
  - A window that Windows refused to let it type into (a program running as administrator),
    from the first refusal on.
- **Punctuation & Capitalization**:
  - Words are also fixed as soon as `, ; : ! ?` follows them, and the first word after `. ! ?` gets capitalized. A full stop alone never triggers a fix (`example.com`).
  - Two capitals at the start of a word are fixed (`THe` → `The`), while `IDs`, `PCs`, and other acronyms are preserved.
- **Quick Controls**:
  - **Ctrl+Alt+K** or double-click the tray icon pauses and resumes.
  - Tray icon: green = correcting, amber = skipping the program in front or a fullscreen game, grey = paused.

Every one of these features can be toggled in **Settings > Corrections** and **Settings > General**.

## Settings

Open via Tray menu > **Settings and statistics**:
- **General**: On/off, start with Windows, keyboard shortcuts, keyboard layout (Auto/QWERTY/QWERTZ/AZERTY/Dvorak/Colemak), primary English dialect, and variant acceptance.
- **Corrections**: Toggle punctuation triggers, sentence capitalization, two-capital fixes, phrase fixes, double-space period, smart typography, fullscreen skipping, the pill and Smart Compose.
- **My words**: Words that are never corrected.
- **My fixes**: Your own fixes, shortcuts, and snippets written as `typo=correction`, one to four words on the left (e.g. `brb=be right back`, `d={date}`, `clip={clip}`).
- **Programs**: Programs in which correction is bypassed.
- **Typos**: Inspect and manage frequent corrections.
- **Statistics**: Performance counters, latency metrics, and data folder launcher.

Your settings and lists are plain text files in `%APPDATA%\KeyboardWriterHelper`.
Edits made in Notepad are picked up within a second; if a list was changed in the app at the
same time, both sets of changes are kept.

## Word lists

The built-in lists are text files in `data\`, embedded into the executable at compile time:

| File | What it holds |
| --- | --- |
| `fixes.txt` | fixed answers: `dont=don't`, `alot=a lot` |
| `phrase-fixes.txt` | multi-word fixes: `their is=there is`, `could of been=could have been` |
| `never-correct.txt` | commands, code words, slang, and abbreviations to leave alone |
| `other-language.txt` | Hindi and Gujarati words that switch correction off for the line |
| `abbreviations.txt` | words whose full stop does not end a sentence |
| `excluded-apps.txt` | programs skipped on first run |
| `languages.txt` | the English spellings the app can use |

A phrase listed as its own fix (`is there going=is there going`) keeps the shorter fixes away
from it. Add the correct sentences a new phrase fix could break to `tests/must-not-change.txt`.

## Testing & Benchmarks

Run the complete test and evaluation suite:

```cmd
test.cmd
```

It fails when any step fails:

| Step | Command | Today |
| --- | --- | --- |
| Built-in lists: nothing listed twice or contradicting | `--lint` | ok |
| Misspellings fixed (limit: at least 95%, at most 3% wrong) | `--eval --min-right 95 --max-wrong 3 tests\typos.txt tests\reviewer-typos.txt` | 95.7% right, 2.4% wrong |
| Correct words left alone (limit: at most 0.5% changed) | `--eval-ok --max-changed 0.5 tests\correct-words.txt` | 0.3% changed |
| Typing cases, including correct text that must not change | `--cases tests\typing-cases.txt tests\must-not-change.txt` | 243 passed |
| End to end through the real keyboard hook | `--selftest ...` | types into its own window for a few seconds |

During the end-to-end test a small window types by itself: do not touch the keyboard. Set
`KWH_SKIP_SELFTEST=1` to skip it; when it cannot get the keyboard focus it is reported as SKIPPED.

## CI/CD & Releases

Automated workflows are managed via GitHub Actions:
- **CI Workflow (`.github/workflows/ci.yml`)**: Triggered on pushes and pull requests to `main`. Builds both ways (.NET 10 and the Windows compiler), runs the full test suite, and archives build artifacts.
- **Release Workflow (`.github/workflows/release.yml`)**: Triggered on version tags (e.g. `v1.2`) or manually via `workflow_dispatch`. Builds standalone binaries, bundles ZIP distributions, generates SHA-256 checksums, and publishes GitHub releases automatically.

## Data

`english-frequency.txt` is built by `build_frequency.py` from OpenSubtitles 2018 counts (hermitdave/FrequencyWords, CC-BY-SA-4.0) and Google Web Trillion Word Corpus counts (norvig.com/ngrams). `english-pairs.txt` is built by `build_pairs.py` from the two-word counts of the same Google corpus. `tools/expand_corpus.py` adds `english-trigrams.txt` and packs all three into `english-model.bin`, which is what the exe carries.
