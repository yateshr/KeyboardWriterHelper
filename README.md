# Keyboard Writer Helper

A small, high-performance Windows tray app that fixes spelling mistakes in any program as you type.
When you press Space after a misspelled word, it is replaced with the most likely
intended word (`improrvie ` becomes `improve `). Nothing you type is sent anywhere,
and nothing is saved unless you ask for it (see Typos below).

## Build and run

Dual-compatible with modern **.NET 9 SDK** and vanilla Windows built-in C# compiler (zero external dependencies):

- **Modern .NET SDK (.NET 9)**:
  ```powershell
  dotnet build
  dotnet run
  ```
- **Windows Built-in C# Compiler (classic)**:
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
  - A sleek, floating non-activating visual candidate pill appears near the caret showing all available alternatives and the active selection without stealing focus.
- **Multi-Word Phrase Grammar Correction**:
  - Catches 2-word, 3-word, and 4-word slips: `their is` $\to$ `there is`, `could of been` $\to$ `could have been`, `one in the same` $\to$ `one and the same`, `all of the sudden` $\to$ `all of a sudden`.
- **Smart Typography (Configurable)**:
  - Smart dashes: `-- ` and `--- ` become em-dash `— `.
  - Smart ellipsis: `... ` becomes `… `.
  - Smart arrows: `-> ` becomes `→ `, `=> ` becomes `⇒ `, `<- ` becomes `← `.
- **Dynamic Text Snippets & Macros**:
  - Supports dynamic macros in *My fixes*:
    - `{date}` or `:date:` expands to current date (`YYYY-MM-DD`).
    - `{time}` or `:time:` expands to current time (`HH:mm`).
    - `{datetime}` or `:datetime:` expands to `YYYY-MM-DD HH:mm`.
    - `{clip}` or `:clip:` expands to current clipboard text without MTA COM thread issues.
    - Example: `now={time}`, `today={date}`, `myclip={clip}`.
- **Multi-Keyboard Layout Physical Proximity**:
  - Physical key adjacency scoring supports **QWERTY**, **QWERTZ** (German), **AZERTY** (French), **Dvorak**, and **Colemak**.
  - Automatically detects the active foreground keyboard layout via Windows API (`GetKeyboardLayout`) or can be forced to a specific layout in Settings.
- **Fullscreen Exclusive & Game Detection**:
  - Automatically detects fullscreen exclusive windows, DirectX/OpenGL games, and media players (`SHQueryUserNotificationState`, Direct3D fullscreen checks) to bypass keyboard hooking and eliminate input latency during gameplay.
- **Punctuation & Capitalization**:
  - Words are also fixed as soon as `, ; : ! ?` follows them, and the first word after `. ! ?` gets capitalized. A full stop alone never triggers a fix (`example.com`).
  - Two capitals at the start of a word are fixed (`THe` $\to$ `The`), while `IDs`, `PCs`, and other acronyms are preserved.
- **Quick Controls**:
  - **Ctrl+Alt+K** or double-click the tray icon pauses and resumes.
  - Tray icon: green = correcting, amber = skipping foreground program/fullscreen game, grey = paused.
  - Terminals, password managers, and remote-desktop programs are skipped by default.

Every one of these features can be toggled in **Settings > Corrections** and **Settings > General**.

## Settings

Open via Tray menu > **Settings and statistics**:
- **General**: On/off, start with Windows, keyboard shortcuts, keyboard layout (Auto/QWERTY/QWERTZ/AZERTY/Dvorak/Colemak), primary English dialect, and variant acceptance.
- **Corrections**: Toggle punctuation triggers, sentence capitalization, two-capital fixes, phrase fixes, double-space period, smart dashes, smart arrows, smart ellipsis, and candidate pill popup.
- **My words**: Words that are never corrected.
- **My fixes**: Your own fixes, shortcuts, and snippets written as `typo=correction` (e.g. `brb=be right back`, `d={date}`, `clip={clip}`).
- **Programs**: Programs in which correction is bypassed.
- **Typos**: Inspect and manage frequent corrections.
- **Statistics**: Performance counters, latency metrics, and data folder launcher.

Your settings and lists are plain text files in `%APPDATA%\KeyboardWriterHelper`.
Edits made in Notepad are picked up within a second.

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

## Testing & Benchmarks

Run the complete test and evaluation suite:

```cmd
test.cmd
```

Or run individual benchmarks:

```powershell
# Typing cases (100% pass)
.\KeyboardWriterHelper.exe --cases tests\typing-cases.txt | Out-String

# Typo benchmark (>95% accuracy)
.\KeyboardWriterHelper.exe --eval tests\typos.txt tests\reviewer-typos.txt | Out-String

# False-positive test (0% corruption of correct words)
.\KeyboardWriterHelper.exe --eval-ok tests\correct-words.txt | Out-String
```

## CI/CD & Releases

Automated workflows are managed via GitHub Actions:
- **CI Workflow (`.github/workflows/ci.yml`)**: Triggered on pushes and pull requests to `main`. Builds dual targets (.NET 9 and .NET Framework 4.0), runs the full test suite, and archives build artifacts.
- **Release Workflow (`.github/workflows/release.yml`)**: Triggered on version tags (e.g. `v1.2`) or manually via `workflow_dispatch`. Builds standalone binaries, bundles ZIP distributions, generates SHA-256 checksums, and publishes GitHub releases automatically.

## Data

`english-frequency.txt` is built by `build_frequency.py` from OpenSubtitles 2018 counts (hermitdave/FrequencyWords, CC-BY-SA-4.0) and Google Web Trillion Word Corpus counts (norvig.com/ngrams). `english-pairs.txt` is built by `build_pairs.py` from the two-word counts of the same Google corpus.
