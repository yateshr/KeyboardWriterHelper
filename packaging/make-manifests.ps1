# Writes the winget and Scoop manifests for a published release, into packaging\out\.
# It reads the file checksums from the release itself, so nothing is typed by hand.
#
#   powershell -File packaging\make-manifests.ps1 -Tag v2.2
#
# Nothing is submitted anywhere. To list the app:
#   winget:  copy packaging\out\winget\* to a new folder  manifests\y\YATESHROHIT\KeyboardWriterHelper\<version>\
#            in a fork of https://github.com/microsoft/winget-pkgs and open a pull request
#            (or run: wingetcreate submit packaging\out\winget).
#   Scoop:   put packaging\out\scoop\keyboard-writer-helper.json in a bucket repository.
param([Parameter(Mandatory = $true)][string]$Tag)

$ErrorActionPreference = "Stop"
$repo = "yateshr/KeyboardWriterHelper"
$version = $Tag.TrimStart("v")
$base = "https://github.com/$repo/releases/download/$Tag"
$out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "out"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "$out\winget", "$out\scoop" | Out-Null

$sums = (Invoke-WebRequest "$base/SHA256SUMS.txt" -UseBasicParsing).Content
function Hash([string]$file) {
    $m = [regex]::Match($sums, "(?m)^([0-9A-Fa-f]{64})\s+\*?" + [regex]::Escape($file) + "\s*$")
    if (-not $m.Success) { throw "No checksum for $file in the release." }
    return $m.Groups[1].Value.ToUpper()
}
$setupName = "KeyboardWriterHelper-Setup-$Tag.exe"
$setupHash = Hash $setupName
$portableHash = Hash "KeyboardWriterHelper-net40.exe"
$date = Get-Date -Format "yyyy-MM-dd"
$id = "YATESHROHIT.KeyboardWriterHelper"

# ---- winget (installer) ----
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@ | Set-Content "$out\winget\$id.yaml" -Encoding UTF8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $version
InstallerType: inno
Scope: user
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: $base/$setupName
  InstallerSha256: $setupHash
ManifestType: installer
ManifestVersion: 1.6.0
"@ | Set-Content "$out\winget\$id.installer.yaml" -Encoding UTF8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: en-US
Publisher: YATESH ROHIT
PublisherUrl: https://github.com/yateshr
PackageName: Keyboard Writer Helper
PackageUrl: https://github.com/$repo
License: PolyForm Noncommercial 1.0.0
LicenseUrl: https://github.com/$repo/blob/main/LICENSE
Copyright: Copyright (c) 2026 YATESH ROHIT
ShortDescription: Fixes spelling mistakes in any Windows program as you type.
Description: A small tray app that corrects misspelled words when you press Space, offers other suggestions in a corner of the screen, and keeps everything on your PC. Free for non-commercial use.
Tags:
- spelling
- autocorrect
- typing
- productivity
- keyboard
ReleaseNotesUrl: https://github.com/$repo/releases/tag/$Tag
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@ | Set-Content "$out\winget\$id.locale.en-US.yaml" -Encoding UTF8

# ---- Scoop (portable) ----
@"
{
    "version": "$version",
    "description": "Fixes spelling mistakes in any Windows program as you type.",
    "homepage": "https://github.com/$repo",
    "license": {
        "identifier": "Noncommercial",
        "url": "https://github.com/$repo/blob/main/LICENSE"
    },
    "url": "$base/KeyboardWriterHelper-net40.exe#/KeyboardWriterHelper.exe",
    "hash": "$($portableHash.ToLower())",
    "bin": "KeyboardWriterHelper.exe",
    "shortcuts": [
        [ "KeyboardWriterHelper.exe", "Keyboard Writer Helper" ]
    ],
    "checkver": "github",
    "autoupdate": {
        "url": "https://github.com/$repo/releases/download/v`$version/KeyboardWriterHelper-net40.exe#/KeyboardWriterHelper.exe"
    }
}
"@ | Set-Content "$out\scoop\keyboard-writer-helper.json" -Encoding UTF8

Write-Host "Manifests for $Tag written to $out"
Get-ChildItem $out -Recurse -File | ForEach-Object { Write-Host "  " $_.FullName.Substring($out.Length + 1) }
