@echo off
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /t:winexe /codepage:65001 /warn:4 /win32manifest:app.manifest /out:"%~dp0Screener.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0Screener.cs"
if errorlevel 1 exit /b 1
echo OK: %~dp0Screener.exe
