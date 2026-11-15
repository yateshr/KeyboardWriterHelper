# Releasing

Pushing a tag that starts with `v` runs `.github/workflows/release.yml`, which builds the exe, runs the
tests, builds the installer, tests it, and publishes the GitHub release with these files:

- `KeyboardWriterHelper-Setup-<tag>.exe`: the installer (installs for the user, no administrator)
- `KeyboardWriterHelper-net40.exe` and `KeyboardWriterHelper-<tag>-net40.zip`: the portable version
- `KeyboardWriterHelper-net10-x64.exe` and its zip: the same app on .NET 10
- `KeyboardWriterHelper-logo.png`, `KeyboardWriterHelper-icon.ico`, `SHA256SUMS.txt`

The release notes are the section of `CHANGELOG.md` with the same version. **The app's own updater reads the
latest release**: it looks for `KeyboardWriterHelper-net40.exe` (or the net10 one) and `SHA256SUMS.txt`, so
keep those two file names.

## Checklist

1. `build.cmd bump` (unless the version is already new), then write the `## <version>` entry in `CHANGELOG.md`.
2. `build.cmd`, then `test.cmd`. Also run `installer\build-installer.cmd` and `installer\test-installer.cmd`.
3. Try it by hand: the portable exe, then the installer on a PC that does not have the source. Type in Word
   or Notepad. Open every Settings page. Check Settings > Diagnostics.
4. Commit, push the branch, open a pull request, wait for CI, merge.
5. Tag the merge commit: `git tag -a vX.Y -m "Version X.Y"` and `git push origin vX.Y`.
6. Open the release page and check the files and the notes.
7. Optional: `powershell -File packaging\make-manifests.ps1 -Tag vX.Y` writes the winget and Scoop manifests.

## Code signing (optional)

Unsigned files make Windows SmartScreen warn first-time users. To sign, add two repository secrets:
`SIGN_PFX_BASE64` (the certificate as a Base64 `.pfx`) and `SIGN_PFX_PASSWORD`. The release workflow then signs
`KeyboardWriterHelper.exe` before it goes into the installer, and signs the installer too. Without the secrets
the steps are skipped and everything else works as before.
