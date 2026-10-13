@echo off
rem Measures correction quality on the word lists in tests\ and then runs an
rem end-to-end typing test: a small window opens and types by itself for a few
rem seconds. Do not touch the keyboard while it runs.
set EXE="%~dp0KeyboardWriterHelper.exe"
echo --- misspellings that should be fixed ---
%EXE% --eval "%~dp0tests\typos.txt" "%~dp0tests\reviewer-typos.txt" | findstr "^"
echo --- correct words that should be left alone ---
%EXE% --eval-ok "%~dp0tests\correct-words.txt" | findstr "^"
echo --- end-to-end through the real keyboard hook ---
%EXE% --selftest "teh quick brwon fox is improrvie insted of currect. shoulld i recieve it? " "the quick brown fox is improve instead of correct. should I receive it? " | findstr "^"
