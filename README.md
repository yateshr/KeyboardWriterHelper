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
  - With personal learning enabled, undoing a correction twice protects the word or phrase
    across sessions. Learned entries are managed separately from your manual *My words* list.
- **Personal Learning (off by default)**:
  - Enable it in **Settings > Learning** to remember names, vocabulary and preferred fixes locally.
  - Two explicit rejections protect a word or phrase. Choose the same alternative with
    **Ctrl+Alt+N** three times, starting the next word after each choice, to learn a preferred fix.
  - Only the final selected alternative counts. Simply typing on after an automatic fix,
    cycling through candidates, Smart Compose, and capitalization-only changes do not train it.
  - The Learning tab shows both pending feedback and learned entries. Forget selected entries,
    clear all learning, or turn learning off to pause both collection and use of saved preferences.
  - Manual *My words* and *My fixes* take priority. No sentences or clipboard contents are collected.
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
  - Recognizes common noun subjects, including one adjective: `the files is ready` →
    `the files are ready`, `a new file have a name` → `a new file has a name`,
    `every user have access` → `every user has access`.
  - Corrects verb forms after modals, negation and inverted pronoun questions:
    `he doesn't likes it` → `he doesn't like it`, `she will goes` → `she will go`,
    `does he likes it?` → `does he like it?`. One or two short modifiers such as
    `not` and `always` can appear between the auxiliary and the verb.
  - Leaves ambiguous noun groups, relative clauses and commands alone. For example,
    `Everyone have fun` stays unchanged; `Everyone have arrived` waits for `arrived`
    before becoming `Everyone has arrived`. Manual words/fixes and learned protections
    also apply to earlier words in a deferred correction. These rules use **Corrections >
    Fix grammar and phrase mistakes**.
- **Offline Sentence Context**:
  - Ranks spelling candidates against up to six earlier words, using adjacent bigrams,
    trigrams and progressively weaker topic signals from older words.
  - Context may override the strongest spelling-only answer only by a conservative
    confidence margin, so an incidental earlier word cannot force a weak correction.
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

Feature controls are in **Settings > Corrections**, **Settings > General** and **Settings > Learning**.

## Settings

Open via Tray menu > **Settings and statistics**:
- **General**: On/off, start with Windows, keyboard shortcuts, keyboard layout (Auto/QWERTY/QWERTZ/AZERTY/Dvorak/Colemak), primary English dialect, and variant acceptance.
- **Corrections**: Toggle punctuation triggers, sentence capitalization, two-capital fixes, phrase fixes, double-space period, smart typography, fullscreen skipping, the pill and Smart Compose.
- **My words**: Words that are never corrected.
- **My fixes**: Your own fixes, shortcuts, and snippets written as `typo=correction`, one to four words on the left (e.g. `brb=be right back`, `d={date}`, `clip={clip}`).
- **Programs**: Programs in which correction is bypassed.
- **Typos**: Inspect and manage frequent corrections.
- **Learning**: Enable personal learning, inspect feedback counts and learned words/fixes, forget entries, or clear learning.
- **Statistics**: Performance counters, latency metrics, and data folder launcher.

Your settings and lists are plain text files in `%APPDATA%\KeyboardWriterHelper`.
Edits made in Notepad are picked up within a second; if a list was changed in the app at the
same time, both sets of changes are kept. Personal feedback and preferences are stored separately
in `personal-learning.txt`; manage that file through the Learning tab. Turning learning off retains
the entries but stops using and updating them. Clear all learning also clears saved feedback counts,
without changing manual lists. Earlier versions' words already saved in My words remain there.

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

Personal learning and grammar can also be tested without Windows, using .NET 10:

```sh
dotnet run --project tests/Personalization/Personalization.csproj -c Release
dotnet run --project tests/Grammar/Grammar.csproj -c Release
```

These tests compile the production store, policy and engine and exercise learning, restart,
manual overrides, feedback cancellation, file validation and failed-save recovery. Spelling
fixtures replace the Windows dictionary for these tests; the Windows suite below still validates
the real dictionary and keyboard hook.

The grammar suite uses the production engine with a deterministic spelling fixture and
the shared `tests/grammar-cases.txt` and `tests/must-not-change.txt` corpora. It checks
agreement, auxiliary forms, unchanged valid English, manual overrides, undo, candidate
switching, and personal learning. `test.cmd` runs the same sentence corpora against the
real Windows dictionary; CI also runs both portable suites on Linux and Windows.

`test.cmd` fails when any step fails. The Windows figures below are the previous
baseline; rerun Windows CI to measure the expanded grammar corpus and current spelling results.

| Step | Command | Previous Windows baseline |
| --- | --- | --- |
| Built-in lists: nothing listed twice or contradicting | `--lint` | ok |
| Misspellings fixed (limit: at least 95%, at most 3% wrong) | `--eval --min-right 95 --max-wrong 3 tests\typos.txt tests\reviewer-typos.txt` | 95.7% right, 2.4% wrong |
| Correct words left alone (limit: at most 0.5% changed) | `--eval-ok --max-changed 0.5 tests\correct-words.txt` | 0.3% changed |
| Typing cases, including correct text that must not change | `--cases tests\typing-cases.txt tests\must-not-change.txt tests\grammar-cases.txt` | Expanded corpus; rerun on Windows |
| End to end through the real keyboard hook | `--selftest ...` | types into its own window for a few seconds |

During the end-to-end test a small window types by itself: do not touch the keyboard. Set
`KWH_SKIP_SELFTEST=1` to skip it; when it cannot get the keyboard focus it is reported as SKIPPED.

## CI/CD & Releases

Automated workflows are managed via GitHub Actions:
- **CI Workflow (`.github/workflows/ci.yml`)**: Triggered on pushes and pull requests to `main`. Builds both ways (.NET 10 and the Windows compiler), runs the full test suite, and archives build artifacts.
- **Release Workflow (`.github/workflows/release.yml`)**: Triggered on version tags (e.g. `v1.2`) or manually via `workflow_dispatch`. Builds standalone binaries, bundles ZIP distributions, generates SHA-256 checksums, and publishes GitHub releases automatically.

## Data

`english-frequency.txt` is built by `build_frequency.py` from OpenSubtitles 2018 counts (hermitdave/FrequencyWords, CC-BY-SA-4.0) and Google Web Trillion Word Corpus counts (norvig.com/ngrams). `english-pairs.txt` is built by `build_pairs.py` from the two-word counts of the same Google corpus. `tools/expand_corpus.py` adds `english-trigrams.txt` and packs all three into `english-model.bin`, which is what the exe carries.
