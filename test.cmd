@echo off
rem Checks the built-in lists, measures correction quality on the word lists in
rem tests\, runs the typing cases through the engine, and then runs an
rem end-to-end typing test: a small window opens and types by itself for a few
rem seconds. Do not touch the keyboard while it runs.
rem
rem   set KWH_SKIP_SELFTEST=1    skip the end-to-end test (no desktop, or busy)
rem
rem The quality limits are just below today's results, so a change that makes
rem corrections worse, or changes more correct words, fails the run.
setlocal enabledelayedexpansion

set MIN_RIGHT=95.0
set MAX_WRONG=3.0
set MAX_CHANGED=0.5

set EXE="%~dp0KeyboardWriterHelper.exe"
if not exist %EXE% set EXE="%~dp0bin\Debug\net10.0-windows\KeyboardWriterHelper.exe"
if not exist %EXE% (
    echo [ERROR] KeyboardWriterHelper.exe not found. Run build.cmd or dotnet build first.
    exit /b 1
)

set FAILED=0
set LOG="%TEMP%\kwh_test_%RANDOM%.log"

%EXE% --version > %LOG% 2>&1
type %LOG%
del %LOG% >nul 2>&1

call :run "built-in lists" --lint
call :run "misspellings that should be fixed" --eval --min-right %MIN_RIGHT% --max-wrong %MAX_WRONG% "%~dp0tests\typos.txt" "%~dp0tests\reviewer-typos.txt"
call :run "correct words that should be left alone" --eval-ok --misses --max-changed %MAX_CHANGED% "%~dp0tests\correct-words.txt"
call :run "typing cases through the engine" --cases "%~dp0tests\typing-cases.txt" "%~dp0tests\must-not-change.txt" "%~dp0tests\grammar-cases.txt"

echo --- end-to-end through the real keyboard hook ---
if defined KWH_SKIP_SELFTEST (
    echo [SKIPPED] KWH_SKIP_SELFTEST is set
    goto summary
)
%EXE% --selftest "teh quick brwon fox is improrvie, insted of currect. shoulld i recieve it? yes, their is. THe wether {NEXT}is ok " "the quick brown fox is improve, instead of correct. Should I receive it? Yes, there is. The whether is ok " > %LOG% 2>&1
set ERR=!ERRORLEVEL!
type %LOG%
del %LOG% >nul 2>&1
if "!ERR!"=="2" (
    echo [SKIPPED] the end-to-end test could not run here ^(no keyboard focus^)
) else if not "!ERR!"=="0" (
    echo [FAIL] --selftest failed with exit code !ERR!
    set FAILED=1
)

:summary
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

rem call :run "title" arguments...  runs the exe with the arguments; a non-zero
rem exit code fails the whole run.
:run
echo --- %~1 ---
%EXE% %2 %3 %4 %5 %6 %7 %8 %9 > %LOG% 2>&1
set ERR=!ERRORLEVEL!
type %LOG%
del %LOG% >nul 2>&1
if not "!ERR!"=="0" (
    echo [FAIL] %2 failed with exit code !ERR!
    set FAILED=1
)
exit /b 0
