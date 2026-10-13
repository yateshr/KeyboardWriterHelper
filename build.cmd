@echo off
rem Builds KeyboardWriterHelper.exe with the C# compiler that ships with Windows.
rem english-frequency.txt is embedded in the exe; build_frequency.py regenerates it.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:"%~dp0KeyboardWriterHelper.exe" /resource:"%~dp0english-frequency.txt",english-frequency.txt "%~dp0KeyboardWriterHelper.cs"
