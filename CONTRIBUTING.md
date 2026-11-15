# Contributing

Thanks for wanting to help. Keyboard Writer Helper is free for non-commercial use (see [LICENSE](LICENSE));
by sending a change you agree that it can be shared under the same licence.

## The one rule

**Changing correct text is worse than missing a mistake.** When the app is not sure, it leaves the word
alone. A fix that is right 95% of the time but wrecks the other 5% does not belong in the built-in data.
Anyone can still add their own rules in Settings > My fixes.

## Build and test (no extra packages)

```cmd
build.cmd                      builds KeyboardWriterHelper.exe with the compiler that ships with Windows
test.cmd                       lists, word-list quality, typing cases, settings window layout, end-to-end typing
dotnet build                   the same code with the .NET SDK
installer\build-installer.cmd  the installer (needs Inno Setup 6.5 or newer)
```

Close the app from its tray menu before building; Windows locks the exe while it runs. To build to another
file, set `KWH_OUT`. `set KWH_SKIP_SELFTEST=1` skips the end-to-end test, which types by itself for a few seconds.

## Where things are

| Folder | What |
| --- | --- |
| `src/Engine` | the typing state machine and the grammar rules (no Windows dependencies) |
| `src/Spelling` | the ranker (which word is meant) and the Windows spell checker |
| `src/Hook` | the keyboard hook and the keystrokes it types |
| `src/UI` | tray app, settings window, suggestion bar, dialogs, `Theme.cs` (all colours and type) |
| `data/` | built-in fixes, phrase fixes, never-correct words, other-language words |
| `tests/` | typing cases, words that must not change, misspelling sets |
| `installer/` | Inno Setup script, pictures and texts |
| `third_party/` | the open data we use, with licences. Credit: `THIRD_PARTY_NOTICES.md` |

## Adding fixes

- A misspelling that is never a real word: add `typo=correction` to `data/fixes.txt`, and a case to
  `tests/typing-cases.txt`.
- A real word used wrongly: only if it is practically never right as typed (`data/phrase-fixes.txt`
  explains the bar). Add a line to `tests/must-not-change.txt` for the correct use you are protecting.
- Run `build.cmd` and `test.cmd`. `KeyboardWriterHelper.exe --lint` checks the data files.

## Changing how it looks

Everything visual goes through `src/UI/Theme.cs`. Do not hard-code colours or font sizes. After a change,
`KeyboardWriterHelper.exe --settings-shot some-folder` writes a PNG of every page, and `--settings-check`
(part of `test.cmd`) opens every page at three window sizes and reports clipped or overflowing controls.
The window follows the Windows light or dark setting; `set KWH_THEME=light` or `dark` forces one.

## Versions and releases

Versions go 1.1 ... 1.10, then 2.1 ... 2.10, then 3.1. `build.cmd bump` moves to the next one. The steps for a
release are in [docs/RELEASING.md](docs/RELEASING.md).
