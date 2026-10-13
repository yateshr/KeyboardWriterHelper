@echo off
rem Measures correction quality on the word lists in tests\, runs the typing
rem cases through the engine, and then runs an end-to-end typing test: a small
rem window opens and types by itself for a few seconds. Do not touch the
rem keyboard while it runs.
set EXE="%~dp0KeyboardWriterHelper.exe"
%EXE% --version | findstr "^"
echo --- misspellings that should be fixed ---
%EXE% --eval "%~dp0tests\typos.txt" "%~dp0tests\reviewer-typos.txt" | findstr "^"
echo --- correct words that should be left alone ---
%EXE% --eval-ok "%~dp0tests\correct-words.txt" | findstr "^"
echo --- typing cases through the engine ---
%EXE% --cases "%~dp0tests\typing-cases.txt" | findstr "^"
echo --- end-to-end through the real keyboard hook ---
%EXE% --selftest "teh quick brwon fox is improrvie, insted of currect. shoulld i recieve it? yes, their is. THe wether {NEXT}is ok " "the quick brown fox is improve, instead of correct. Should I receive it? Yes, there is. The whether is ok " | findstr "^"
