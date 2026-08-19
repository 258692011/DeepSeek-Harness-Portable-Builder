param(
    # Root of the portable install. Defaults to this script's own directory so
    # the script can sit at the portable root and be run by double-click.
    [string]$PortableRoot = $PSScriptRoot,
    # npm registry for the update (official pinned by default: mirror
    # registries break cross-platform optional deps with UND_ERR_DESTROYED).
    [string]$Registry = 'https://registry.npmjs.org/',
    # Skip the version check and reinstall regardless.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$nodeExe   = Join-Path $PortableRoot 'node\node.exe'
$npmCli    = Join-Path $PortableRoot 'node\node_modules\npm\bin\npm-cli.js'
$appDir    = Join-Path $PortableRoot 'app'
$pkgJson   = Join-Path $appDir 'package.json'
$dshEntry  = Join-Path $appDir 'node_modules\@deepseek-ai\dsh\lib\bin.js'

foreach ($need in @($nodeExe, $npmCli, $pkgJson)) {
    if (-not (Test-Path $need)) {
        Write-Host "ERROR: missing $need - is this script inside a DeepSeek-Harness-Portable install?" -ForegroundColor Red
        exit 1
    }
}

# Refuse to update while the launcher or a dsh web process from THIS portable
# is still running (files would be locked / a half-updated tree could serve).
$running = @()
foreach ($proc in (Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue)) {
    if ($proc.CommandLine -and $proc.CommandLine -like "*$PortableRoot*" -and $proc.CommandLine -like '*bin.js*') {
        $running += $proc.ProcessId
    }
}
$launcher = Get-Process -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue
if ($running.Count -gt 0 -or $launcher) {
    Write-Host 'ERROR: DeepSeek Harness is still running.' -ForegroundColor Red
    Write-Host 'Close it (tray icon -> 退出) before updating, then run this script again.' -ForegroundColor Yellow
    if ($running.Count -gt 0) { Write-Host ('  dsh web processes: ' + ($running -join ', ')) }
    if ($launcher) { Write-Host ('  DeepSeek Harness.exe PIDs: ' + (($launcher | ForEach-Object { $_.Id }) -join ', ')) }
    exit 1
}

$current = (Get-Content $pkgJson -Raw | ConvertFrom-Json).dependencies.'@deepseek-ai/dsh'

Write-Host "DeepSeek Harness Portable 更新工具"
Write-Host ('当前版本: ' + $current)
Write-Host '正在查询 npm registry 最新版本...'

# Ask the registry for the latest dist-tag. Failure here just means we fall
# through to a plain reinstall (npm will still figure out what to do).
$latest = $null
try {
    $latest = & $nodeExe $npmCli view '@deepseek-ai/dsh' version --registry $Registry 2>$null | Select-Object -Last 1
} catch { }
$latest = ($latest | ForEach-Object { $_.Trim() } | Where-Object { $_ }) | Select-Object -Last 1

if (-not $Force -and $latest -and $latest -eq $current) {
    Write-Host "已是最新版本 ($latest),无需更新。" -ForegroundColor Green
    exit 0
}
if ($latest) {
    Write-Host ("registry 最新: " + $latest)
    if (-not $Force) {
        Write-Host "确认更新到 $latest ?" -ForegroundColor Yellow
        $ans = Read-Host '输入 y 继续 (Enter 取消)'
        if ($ans -notin @('y', 'Y', 'yes', 'YES')) {
            Write-Host '已取消。'
            exit 0
        }
    }
} else {
    Write-Host '无法查询 registry(可能离线),将尝试直接安装 @deepseek-ai/dsh@latest。' -ForegroundColor Yellow
}

Write-Host '正在更新 (app 目录, 使用包内 npm)...'
Push-Location $appDir
try {
    & $nodeExe $npmCli install "@deepseek-ai/dsh@latest" --registry $Registry --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

# Verify the installed dsh runs.
if (-not (Test-Path $dshEntry)) { throw "dsh entry missing after update: $dshEntry" }
$newVer = & $nodeExe $dshEntry --version 2>&1 | Select-Object -Last 1
Write-Host ("更新完成: " + ($newVer -replace '\s+', ' ').Trim()) -ForegroundColor Green
Write-Host '请重启 DeepSeek Harness.exe 使用新版本。' -ForegroundColor Cyan
Write-Host '提示: 更新只动 app\node_modules;用户数据 (data\dsh\profiles 等) 原样保留。'
