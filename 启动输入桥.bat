@echo off
rem Prefer the native EXE; otherwise use the hidden PowerShell launcher.
if exist "%~dp0TypeBridge.exe" (
    start "" "%~dp0TypeBridge.exe"
) else (
    wscript.exe "%~dp0启动输入桥.vbs"
)
