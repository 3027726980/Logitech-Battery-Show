# GPW2 Battery Show 编译脚本（Windows 自带 csc.exe，无需安装 SDK）
param(
    [string]$Version = "1.0"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compilerCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw "未找到 .NET Framework C# 编译器（csc.exe）。"
}

$distRoot = Join-Path $repositoryRoot "dist"
New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

$executable = Join-Path $distRoot "GPW2BatteryShow.exe"
$outputArgument = "/out:" + $executable
$hidSharp = Join-Path $repositoryRoot "lib\HidSharp.dll"
$hidSharpReference = "/reference:" + $hidSharp

$sources = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src") -Filter *.cs |
    ForEach-Object { $_.FullName }

& $compiler `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    /debug- `
    $outputArgument `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    $hidSharpReference `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出码：$LASTEXITCODE"
}

Copy-Item -LiteralPath $hidSharp -Destination $distRoot -Force

Write-Host "构建完成：$executable"
