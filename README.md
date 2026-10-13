# Keyboard Writer Helper

A small Windows tray app that fixes spelling mistakes in any program as you type.
When you press Space after a misspelled word it is replaced with the most likely
intended word (`improrvie ` becomes `improve `). Nothing you type is saved or sent anywhere.

## Build and run
Needs only Windows (uses the C# compiler that ships with it).

    build.cmd
    KeyboardWriterHelper.exe

A green "Aa" icon appears in the system tray.

## Use
- Press **Backspace** right after a fix to undo it (the word is then left as typed).
- **Ctrl+Alt+K** (left Alt) or double-click the tray icon pauses and resumes.
- Words are also fixed as soon as `, ; : ! ?` follows them, and the first word after `. ! ?` gets a capital
  (both can be switched off in Settings). A full stop alone never triggers a fix (`example.com`).
- Tray icon: green = correcting, amber = skipping the program in front, grey = paused.
- Tray menu: turn off/on in the current program, recent corrections (click one to never correct that word),
  settings and statistics, start with Windows.
- Settings window: options, My words, skipped programs, session and all-time counters.
- Undo the same fix twice and the word is added to My words automatically.
- Terminals, password managers and remote-desktop programs are skipped by default
  (`%APPDATA%\KeyboardWriterHelper\excluded-apps.txt`).

## Test
`test.cmd` measures accuracy on the word lists in `tests/` and runs one typing test
through the real keyboard hook (a window opens and types by itself; do not touch the keyboard).

## Limits
Only fixes non-words. Real words used wrongly ("her" for "here") are not caught.
The last word before Enter or Tab is not corrected, by design.

## Data
`english-frequency.txt` is built by `build_frequency.py` from OpenSubtitles 2018 counts
(hermitdave/FrequencyWords, CC-BY-SA-4.0) and Google Web Trillion Word Corpus counts (norvig.com/ngrams).
