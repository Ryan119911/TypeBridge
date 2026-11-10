@echo off
chcp 65001 >nul
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo 未找到 .NET Framework 自带的 C# 编译器。
    echo 请确认系统已安装 .NET Framework 4.x。
    pause
    exit /b 1
)

echo 正在编译 TypeBridge.exe ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:"%~dp0assets\typebridge.ico" /out:"%~dp0TypeBridge.exe" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "%~dp0TypeBridge.cs"
if errorlevel 1 (
    echo 编译失败，请查看上面的错误信息。
    pause
    exit /b 1
)

echo 编译成功：%~dp0TypeBridge.exe
pause
