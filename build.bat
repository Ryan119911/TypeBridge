@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo .NET Framework C# compiler was not found.
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:"%~dp0assets\typebridge.ico" /out:"%~dp0TypeBridge.exe" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "%~dp0TypeBridge.cs"
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

echo Build succeeded: %~dp0TypeBridge.exe
pause
