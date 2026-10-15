@echo off
rem Builds KeyboardWriterHelper.exe with the C# compiler that ships with Windows.
rem The word lists (data\*.txt) and the two count lists (english-*.txt) are
rem compiled into the exe.
rem
rem   build.cmd         build the version named in version.txt
rem   build.cmd bump    move version.txt to the next version first, then build
rem
rem Versions run 1.1, 1.2 ... 1.10, then 2.1 ... 2.10, then 3.1 and so on.
rem The revision is the number of git commits (0 without git). A "+" after it
rem means the build contains changes that were not committed yet.
rem
rem To build to another file (the app locks its exe while it runs):
rem   set KWH_OUT=test.exe
setlocal
cd /d "%~dp0"

set /p VERSION=<version.txt
for /f "tokens=1,2 delims=." %%a in ("%VERSION%") do (set MAJOR=%%a& set MINOR=%%b)
if /i "%~1"=="bump" (
    if %MINOR% GEQ 10 (set /a MAJOR+=1 & set MINOR=1) else (set /a MINOR+=1)
)
if /i "%~1"=="bump" >version.txt echo %MAJOR%.%MINOR%

set REV=0
set DIRTY=
for /f %%c in ('git rev-list --count HEAD 2^>nul') do set REV=%%c
git diff --quiet HEAD 2>nul
if errorlevel 1 if not "%REV%"=="0" set DIRTY=+
set SHOWN=%MAJOR%.%MINOR% (rev %REV%%DIRTY%)

set GEN=%TEMP%\KeyboardWriterHelper.Version.cs
>"%GEN%" echo [assembly: System.Reflection.AssemblyTitle("Keyboard Writer Helper")]
>>"%GEN%" echo [assembly: System.Reflection.AssemblyProduct("Keyboard Writer Helper")]
>>"%GEN%" echo [assembly: System.Reflection.AssemblyVersion("%MAJOR%.%MINOR%.%REV%.0")]
>>"%GEN%" echo [assembly: System.Reflection.AssemblyFileVersion("%MAJOR%.%MINOR%.%REV%.0")]
>>"%GEN%" echo [assembly: System.Reflection.AssemblyInformationalVersion("%SHOWN%")]

set RES=/resource:english-frequency.txt,english-frequency.txt /resource:english-pairs.txt,english-pairs.txt
if exist english-trigrams.txt set RES=%RES% /resource:english-trigrams.txt,english-trigrams.txt
if exist english-model.bin set RES=%RES% /resource:english-model.bin,english-model.bin
for %%f in (data\*.txt) do call set RES=%%RES%% /resource:%%f,%%~nxf

set OUT=%KWH_OUT%
if not defined OUT set OUT=KeyboardWriterHelper.exe
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32manifest:app.manifest /out:"%OUT%" %RES% /recurse:src\*.cs "%GEN%"
if errorlevel 1 goto failed
echo Built %OUT%  -  version %SHOWN%
exit /b 0

:failed
echo.
echo Build failed. If it could not write the exe, the app is still running:
echo exit it from the tray menu and build again.
exit /b 1
