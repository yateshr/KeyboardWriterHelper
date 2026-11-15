# Changelog

Newest first. Versions 1.1 to 1.10, then 2.1, 2.2... (see `version.txt`).

## 2.2

- **New look.** The settings window is redesigned: a side menu (General, Corrections,
  My words, My fixes, Programs, Typos, Learning, Statistics, About), cards with on/off
  switches and a short explanation under each setting, big-number statistics, and
  proper type (Segoe UI and Semibold in a fixed scale that follows Windows display scaling).
- Light and dark, following the Windows app setting, including the title bar, scrollbars,
  lists, drop-downs and the tray menu. The window can be resized.
- The suggestion bar in the top-right corner and every message (update available, update
  problems, confirmations) use the same colours and type, instead of the grey Windows boxes.
- **New logo**: a green keycap with an A and a check badge, drawn from shapes so it is sharp at every size.
  Used for the app icon, the tray icon (green, grey or amber), the settings window and the installer.
- About shows who made the app.
- **Installer.** There are now two downloads: the portable exe as before, and a setup
  (`KeyboardWriterHelper-Setup-<version>.exe`) with a welcome picture, a "before you install"
  page, what is new, credits and "made by", a "you are all set" page, and a joke at the end.
  It installs for your account only (no administrator), adds a Start menu entry, optionally starts
  with Windows, and uninstalls cleanly from Windows Settings, asking whether to keep your settings.
  The built-in updater keeps working on an installed copy.
- Fully usable from the keyboard (Tab, arrow keys in the side menu, Space on switches).
- New About page: version, update check, privacy and credits.
- Fixed `resrat` becoming `resort` instead of `restart`.

## 2.1

- **Automatic updates.** Half a minute after start, and then once a day, the app looks at the
  latest GitHub release. If there is no newer version it says nothing. If there is one, a
  tray notice appears; click it (or "Update to ..." in the tray menu) and the app downloads
  the new exe, checks its SHA-256 against the release's `SHA256SUMS.txt`, replaces itself
  and restarts. A download that does not match is thrown away.
- Tray menu: "Check for updates". Settings: turn the checks off.

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
