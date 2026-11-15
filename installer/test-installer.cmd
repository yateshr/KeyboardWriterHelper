@echo off
rem Installs the built installer silently into a temporary folder, checks the
rem result, and uninstalls it again. Nothing is left behind.
rem Run after build-installer.cmd.
setlocal enabledelayedexpansion
cd /d "%~dp0\.."

set /p VERSION=<version.txt
set SETUP=installer\Output\KeyboardWriterHelper-Setup-%VERSION%.exe
if not exist "%SETUP%" (
    echo [FAIL] %SETUP% not found. Run installer\build-installer.cmd first.
    exit /b 1
)

rem A running copy of the app makes setup refuse (on purpose), so there is nothing to test then.
tasklist /FI "IMAGENAME eq KeyboardWriterHelper*" /NH 2>nul | find /I "KeyboardWriterHelper" >nul
if not errorlevel 1 (
    echo [SKIPPED] Keyboard Writer Helper is running here; exit it from the tray and run this again.
    exit /b 0
)

set TARGET=%TEMP%\kwh-install-test-%RANDOM%
echo --- silent install into %TARGET% ---
"%SETUP%" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="%TARGET%" /TASKS=""
if errorlevel 1 (echo [FAIL] install failed with exit code !ERRORLEVEL! & exit /b 1)

if not exist "%TARGET%\KeyboardWriterHelper.exe" (echo [FAIL] the exe was not installed & exit /b 1)
for %%f in (CHANGELOG.md THIRD_PARTY_NOTICES.md README.md) do (
    if not exist "%TARGET%\%%f" (echo [FAIL] %%f was not installed & exit /b 1)
)
"%TARGET%\KeyboardWriterHelper.exe" --version
if errorlevel 1 (echo [FAIL] the installed exe does not run & exit /b 1)

echo --- silent uninstall ---
for %%u in ("%TARGET%\unins*.exe") do "%%~u" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
rem the uninstaller finishes in the background
for /l %%i in (1,1,20) do (
    if not exist "%TARGET%" goto removed
    ping -n 2 127.0.0.1 >nul
)
echo [FAIL] the install folder is still there after uninstalling
exit /b 1

:removed
echo [OK] installed, ran and uninstalled cleanly
exit /b 0
