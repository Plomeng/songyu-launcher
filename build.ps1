# 发布自包含版本（目录形式）
#
#   .\build.ps1              发布到 .\app
#   .\build.ps1 -Output out  发布到 .\out
#
# 使用目录形式，便于检查和替换各个运行时文件。
#
# 为什么必须 self-contained：
#   目标机器可能只装了旧版 .NET 运行时，双击依赖 .NET 10 的 exe 会直接
#   弹「You must install or update .NET」。自带运行时后任何 Win10/11 x64 都能跑。

param(
    [string]$Output = "app"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "正在发布自包含版本 …" -ForegroundColor Cyan

dotnet publish "src\Songyu.App\Songyu.App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -m:1 `
    -nodeReuse:false `
    -p:UseSharedCompilation=false `
    -o (Join-Path $root $Output)

if ($LASTEXITCODE -ne 0) {
    Write-Host "发布失败。" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 附带命令行入口，便于显示启动失败时的退出码
$launcherSource = Join-Path $root "启动.cmd"
if (Test-Path $launcherSource)
{
    Copy-Item $launcherSource (Join-Path $root $Output) -Force
}

$exe = Join-Path $root "$Output\Songyu.exe"
$files = Get-ChildItem (Join-Path $root $Output) -Recurse -File
$size = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)

Write-Host "完成：$Output\Songyu.exe" -ForegroundColor Green
Write-Host "  共 $($files.Count) 个文件，$size MB（自带 .NET 10 运行时）" -ForegroundColor Green
Write-Host "  启动方式：双击 $Output\启动.cmd" -ForegroundColor Yellow
