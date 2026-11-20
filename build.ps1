param([string]$Output = (Join-Path $PSScriptRoot 'TypeBridge.exe'), [switch]$Test)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $compiler)) { throw 'Missing .NET Framework C# compiler' }
$wpf = Join-Path (Split-Path $compiler) 'WPF'
$xaml = Get-ChildItem "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\System.Xaml" -Filter System.Xaml.dll -Recurse |
    Select-Object -First 1 -ExpandProperty FullName
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll',
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", $xaml)
$arguments = @('/nologo', '/optimize+', '/codepage:65001', '/platform:anycpu')
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$sources = @(Get-ChildItem $PSScriptRoot -Filter '*.cs' | ForEach-Object { $_.FullName })
$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path $Output) -Force | Out-Null
& $compiler @arguments /target:winexe "/win32icon:$PSScriptRoot\assets\typebridge.ico" "/out:$Output" @sources
if ($LASTEXITCODE -ne 0) { throw 'Windows compilation failed' }
Get-Item $Output | Select-Object FullName, Length
if ($Test) {
    $testOutput = Join-Path (Split-Path $Output) 'TypeBridge.Tests.exe'
    $testSources = @(Get-ChildItem "$PSScriptRoot\tests" -Filter '*.cs' | ForEach-Object { $_.FullName })
    & $compiler @arguments /target:exe /main:TypeBridgeApp.Tests "/out:$testOutput" @sources @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
    & $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed' }
}
