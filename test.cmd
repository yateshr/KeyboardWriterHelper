@echo off
rem Measures correction quality on the word lists in tests\, runs the typing
rem cases through the engine, and then runs an end-to-end typing test: a small
rem window opens and types by itself for a few seconds. Do not touch the
rem keyboard while it runs.
setlocal enabledelayedexpansion

set EXE="%~dp0KeyboardWriterHelper.exe"
if not exist %EXE% set EXE="%~dp0bin\Debug\net9.0-windows\KeyboardWriterHelper.exe"
if not exist %EXE% (
    echo [ERROR] KeyboardWriterHelper.exe not found. Run build.cmd or dotnet build first.
    exit /b 1
)

set FAILED=0
set LOG="%TEMP%\kwh_test_%RANDOM%.log"

%EXE% --version > %LOG% 2>&1
type %LOG%
del %LOG% >nul 2>&1

echo --- misspellings that should be fixed ---
%EXE% --eval "%~dp0tests\typos.txt" "%~dp0tests\reviewer-typos.txt" > %LOG% 2>&1
set ERR=!ERRORLEVEL!
type %LOG%
del %LOG% >nul 2>&1
if not "!ERR!"=="0" (
    echo [FAIL] --eval failed with exit code !ERR!
    set FAILED=1
)

echo --- correct words that should be left alone ---
%EXE% --eval-ok "%~dp0tests\correct-words.txt" > %LOG% 2>&1
set ERR=!ERRORLEVEL!
type %LOG%
del %LOG% >nul 2>&1
if not "!ERR!"=="0" (
    echo [FAIL] --eval-ok failed with exit code !ERR!
    set FAILED=1
)

echo --- typing cases through the engine ---
%EXE% --cases "%~dp0tests\typing-cases.txt" > %LOG% 2>&1
set ERR=!ERRORLEVEL!
type %LOG%
del %LOG% >nul 2>&1
if not "!ERR!"=="0" (
    echo [FAIL] --cases failed with exit code !ERR!
    set FAILED=1
)

echo --- end-to-end through the real keyboard hook ---
%EXE% --selftest "teh quick brwon fox is improrvie, insted of currect. shoulld i recieve it? yes, their is. THe wether {NEXT}is ok " "the quick brown fox is improve, instead of correct. Should I receive it? Yes, there is. The whether is ok " > %LOG% 2>&1
set ERR=!ERRORLEVEL!
type %LOG%
del %LOG% >nul 2>&1
if not "!ERR!"=="0" if not "!ERR!"=="2" (
    echo [FAIL] --selftest failed with exit code !ERR!
    set FAILED=1
)

if "!FAILED!"=="0" (
    echo.
    echo ==============================
    echo ALL AUTOMATED TESTS SUCCEEDED
    echo ==============================
    exit /b 0
) else (
    echo.
    echo ==============================
    echo TESTS FAILED
    echo ==============================
    exit /b 1
)
