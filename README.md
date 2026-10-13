# Keyboard Writer Helper

A small Windows tray app that fixes spelling mistakes in any program as you type.
When you press Space after a misspelled word it is replaced with the most likely
intended word (`improrvie ` becomes `improve `). Nothing you type is sent anywhere,
and nothing is saved unless you ask for it (see Typos below).

## Build and run
Needs only Windows (uses the C# compiler that ships with it).

    build.cmd
    KeyboardWriterHelper.exe

A green "Aa" icon appears in the system tray. Exit the app from its tray menu
before building again: Windows locks the exe while it runs.

## Use
- Press **Backspace** right after a fix to undo it (the word is then left as typed).
  Undo the same fix twice and the word is added to My words automatically.
- **Ctrl+Alt+N** right after a fix swaps in the next-best word (`ther ` gives `there`;
  the shortcut then offers `their`, `the`, `they`, and last what you typed).
- **Ctrl+Alt+K** or double-click the tray icon pauses and resumes. The tray menu can
  also pause for 10 minutes or an hour. Both shortcuts can be changed in Settings.
- Words are also fixed as soon as `, ; : ! ?` follows them, and the first word after `. ! ?`
  gets a capital. A full stop alone never triggers a fix (`example.com`).
- Two capitals at the start of a word are fixed (`THe` becomes `The`); `IDs`, `PCs` and
  other acronyms are left alone.
- Real words in the wrong place are fixed when the two-word phrase is practically never
  right: `their is` becomes `there is`, `more then` becomes `more than`, `away form`
  becomes `away from`, `the the` becomes `the`.
- When several words are possible, the word before helps to choose: `was thre` becomes
  `was there`, `the thre` becomes `the three`.
- Optional (off at first): two spaces after a word become a full stop and a space.
- Tray icon: green = correcting, amber = skipping the program in front, grey = paused.
- Terminals, password managers and remote-desktop programs are skipped by default.

Every one of these can be switched off in **Settings > Corrections**.

## Settings
Tray menu > Settings and statistics.
- **General**: on/off, start with Windows, the two shortcuts, which English spelling
  supplies the suggestions, and whether the other spellings count as correct
  (`colour` and `color`).
- **My words**: words that are never corrected.
- **My fixes**: your own fixes and shortcuts, written as `typo=correction`. Works for a
  misspelling (`recieve=receive`), a shortcut (`brb=be right back`) and a two-word slip
  (`could of=could have`).
- **Programs**: programs in which nothing is corrected.
- **Typos**: the fixes the app made, most frequent first, with one click to make a fix
  permanent or to never correct that word. They are kept in memory only, unless you tick
  the box to keep the counts between sessions; they are then saved on this PC, and
  unticking deletes the file.
- **Statistics**: counters (numbers only) and a button that opens the data folder.

Your settings and lists are plain text files in `%APPDATA%\KeyboardWriterHelper`.
Edits made in Notepad are picked up within a second.

## Word lists
No word list is written into the program. The built-in ones are the text files in
`data\`, compiled into the exe by `build.cmd`; each says at its top what it is for.

| File | What it holds |
| --- | --- |
| `fixes.txt` | fixed answers: `dont=don't`, `alot=a lot` |
| `phrase-fixes.txt` | two-word fixes: `their is=there is` |
| `never-correct.txt` | commands, code words, slang and abbreviations to leave alone |
| `other-language.txt` | Hindi and Gujarati words that switch correction off for the line |
| `abbreviations.txt` | words whose full stop does not end a sentence |
| `excluded-apps.txt` | the programs skipped on first run |
| `languages.txt` | the English spellings the app can use |

## Versions
`version.txt` holds the version. Versions run 1.1, 1.2 ... 1.10, then 2.1 ... 2.10,
then 3.1 and so on; `build.cmd bump` moves to the next one and builds it. The revision
after it is the number of git commits, with a `+` when the build contains uncommitted
changes. The version is shown in the tray menu, in Settings > General, in the exe's
file properties, and by `KeyboardWriterHelper.exe --version | more`.

To publish a release on GitHub: `build.cmd bump`, commit, then push a tag such as
`v1.2`. The workflow in `.github\workflows\build.yml` builds the exe on every push and
attaches it to a release when a `v` tag is pushed.

## Test
`test.cmd` measures accuracy on the word lists in `tests/`, runs the typing cases in
`tests\typing-cases.txt` through the engine, and runs one typing test through the real
keyboard hook (a window opens and types by itself; do not touch the keyboard).

## Limits
- English only, on an English keyboard layout.
- A real word used wrongly is only caught when it is in `data\phrase-fixes.txt` or in
  My fixes. `could of` is deliberately not built in, because of "it could of course".
- The last word before Enter or Tab is not corrected, by design.
- Shortcuts in My fixes are made of letters: one word or two.

## Data
`english-frequency.txt` is built by `build_frequency.py` from OpenSubtitles 2018 counts
(hermitdave/FrequencyWords, CC-BY-SA-4.0) and Google Web Trillion Word Corpus counts
(norvig.com/ngrams). `english-pairs.txt` is built by `build_pairs.py` from the two-word
counts of the same Google corpus (norvig.com/ngrams/count_2w.txt).
