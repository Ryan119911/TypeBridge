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

rem ---- 定位 WPF 程序集（设置窗口用 WPF 写）----
set "WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF"
if not exist "%WPF%\PresentationFramework.dll" set "WPF=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF"

set "XAML="
for /f "delims=" %%f in ('dir /b /s "%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\System.Xaml.dll" 2^>nul') do if not defined XAML set "XAML=%%f"

if not exist "%WPF%\PresentationFramework.dll" (
    echo 未找到 WPF 程序集，无法编译设置界面。
    pause
    exit /b 1
)

rem ---- 图标可能在同级 assets，也可能在上一级（本地 src 目录布局）----
set "ICON=%~dp0assets\typebridge.ico"
if not exist "%ICON%" set "ICON=%~dp0..\assets\typebridge.ico"

rem ---- output path: default overwrite TypeBridge.exe in this dir.
rem      Pass one argument to build somewhere else (verify while the app is running).
set "OUT=%~dp0TypeBridge.exe"
if not "%~1"=="" set "OUT=%~1"

echo 正在编译 %OUT% ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
    /win32icon:"%ICON%" ^
    /out:"%OUT%" ^
    /reference:System.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Web.Extensions.dll ^
    /reference:"%WPF%\PresentationFramework.dll" ^
    /reference:"%WPF%\PresentationCore.dll" ^
    /reference:"%WPF%\WindowsBase.dll" ^
    /reference:"%XAML%" ^
    "%~dp0TypeBridge.cs" "%~dp0WpfSettings.cs"
if errorlevel 1 (
    echo 编译失败，请查看上面的错误信息。
    pause
    exit /b 1
)

echo 编译成功：%OUT%
pause
