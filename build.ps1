<#
  Forge Neo 启动器 —— 构建脚本
  产出：_published\ForgeNeoLauncher.exe（自包含单文件，约 70 MB）

  自包含 = 连 .NET 8 桌面运行时一并打进 exe，目标机器不用预装任何东西。
  WPF 不支持裁剪（PublishTrimmed 会破坏 XAML 反射），体积靠单文件压缩来控制。

  用法：
    powershell -ExecutionPolicy Bypass -File build.ps1
#>

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'launcher-src\ForgeNeoLauncher.csproj'
$out  = Join-Path $root '_published'

if (-not (Test-Path $proj)) { throw "找不到工程文件: $proj" }

Write-Host '==> 清理旧产物' -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host '==> 发布（win-x64 / 自包含 / 单文件）' -ForegroundColor Cyan
dotnet publish $proj -c Release -o $out --self-contained true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，exit=$LASTEXITCODE" }

$exe = Join-Path $out 'ForgeNeoLauncher.exe'
if (-not (Test-Path $exe)) { throw "没有产出 exe: $exe" }

$mb = (Get-Item $exe).Length / 1MB
Write-Host ''
Write-Host ('完成: ' + $exe) -ForegroundColor Green
Write-Host ('大小: {0:N1} MB' -f $mb) -ForegroundColor Green
Write-Host ''
Write-Host '把 ForgeNeoLauncher.exe 放进 Forge Neo 根目录（与 launch.py 同级）即可使用。'
