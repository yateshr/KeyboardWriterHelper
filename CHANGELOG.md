# Changelog

Newest first. Versions 1.1 to 1.10, then 2.1 (see `version.txt`).

## 1.10

- The tray icon now uses the new logo (rounded square, "Aa" and a check mark), in green when
  correcting, grey when paused and amber when the program in front is skipped.

## 1.9

- **Keyboard hook watchdog.** Windows can silently drop a keyboard hook, after which
  the app never sees another key and corrects nothing. If you have been typing or
  clicking but no key has reached the app for a few seconds, the hook is installed
  again. `stats.txt` shows how often this happened.
- New app icon and logo (`assets/`), also built into the exe.

## 1.8

- `stats.txt` now lists which programs' keys were ignored and why (excluded program,
  administrator window, full-screen window, password box), as counters only.
- Word, Excel, Outlook, browsers and other writing programs are no longer mistaken
  for full-screen games (Focus mode, F11).

## 1.7

- Built-in fixes from open misspelling data: Wikipedia lists of common misspellings,
  the Birkbeck corpus, Peter Norvig's data, LibreOffice AutoCorrect and the GitHub
  Typo Corpus. About 3,200 pairs, each checked against Windows' own spell checker and
  the app's tests. Credit and licences: `THIRD_PARTY_NOTICES.md`.
- `tools/build_open_fixes.py` rebuilds that list.

## 1.6

- More built-in fixes (hundreds of common misspellings), Windows phrases
  (`windows defender`) and more verbs for subject-verb agreement (`it behave`).
- Words that are not in the frequency list rank lower, so `dehaves` becomes `behaves`.
- `its` becomes `it's` in more places (`its not`, `its a`, `its been`).
- `theres`, `whats`, `youre` and similar contractions are fixed.

## 1.5 and earlier

- The correction popup is shown in the top right corner of the screen being typed on.
- The popup scales with Windows display scaling per monitor and grows on very large
  screens, so it is the same size to the eye everywhere.
- See the GitHub releases for versions 1.1 to 1.5.
