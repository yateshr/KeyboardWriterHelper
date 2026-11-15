@echo off
rem Builds the installer: installer\Output\KeyboardWriterHelper-Setup-<version>.exe
rem
rem   installer\build-installer.cmd          build the exe first, then the installer
rem   set KWH_SKIP_BUILD=1                   use the KeyboardWriterHelper.exe that is already there
rem
rem Needs Inno Setup 6.5 or newer (ISCC.exe): https://jrsoftware.org/isinfo.php
rem The portable version is the plain exe; the installer wraps the same exe.
setlocal
cd /d "%~dp0\.."

if not defined KWH_SKIP_BUILD (
    call build.cmd
    if errorlevel 1 exit /b 1
)
if not exist KeyboardWriterHelper.exe (
    echo KeyboardWriterHelper.exe is missing. Run build.cmd first.
    exit /b 1
)

set /p VERSION=<version.txt

set ISCC=
for %%p in (ISCC.exe) do set ISCC=%%~$PATH:p
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if not defined ISCC (
    echo Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File installer\prepare.ps1 -Version "%VERSION%"
if errorlevel 1 exit /b 1

"%ISCC%" /Qp /DMyVersion=%VERSION% /DAppExe="%CD%\KeyboardWriterHelper.exe" installer\KeyboardWriterHelper.iss
if errorlevel 1 exit /b 1
echo Built installer\Output\KeyboardWriterHelper-Setup-%VERSION%.exe
