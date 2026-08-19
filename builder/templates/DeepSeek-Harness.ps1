param(
    [string]$BuilderRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'
$Repo = Join-Path $BuilderRoot 'upstream'
$StageParent = Join-Path $BuilderRoot 'stage'
$Stage = Join-Path $StageParent 'DeepSeek-Harness-Portable'
$Dist = Join-Path $BuilderRoot 'dist'
$Builder = Join-Path $BuilderRoot 'builder'
$Templates = Join-Path $Builder 'templates'

# Offline caches live under builder\assets (7zip + node + pnpm). The builder
# is fully self-contained: missing assets are downloaded and back-filled,
# never borrowed from another builder.
$SevenZip = Get-ChildItem (Join-Path $Builder 'assets') -Recurse -Filter '7za.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $SevenZip) {
    # Back-fill the cache: 7-Zip ships 7zr.exe (a tiny standalone 7z
    # extractor) plus the "extra" package containing 7za.exe. Unpacking the
    # extra .7z needs only 7zr itself — no circular dependency.
    Write-Host '7za.exe missing; downloading 7-Zip extra package to back-fill the cache...'
    $assets7z = Join-Path $Builder 'assets\7zip'
    New-Item -ItemType Directory -Force $assets7z | Out-Null
    $tmp7zr = Join-Path $env:TEMP '7zr.exe'
    Invoke-WebRequest -Uri 'https://www.7-zip.org/a/7zr.exe' -OutFile $tmp7zr -UseBasicParsing -TimeoutSec 300
    $extra = Join-Path $env:TEMP '7z-extra.7z'
    Invoke-WebRequest -Uri 'https://www.7-zip.org/a/7z2602-extra.7z' -OutFile $extra -UseBasicParsing -TimeoutSec 300
    $extraDir = Join-Path $env:TEMP "7z-extra-$(Get-Random)"
    New-Item -ItemType Directory -Force $extraDir | Out-Null
    & $tmp7zr x $extra "-o$extraDir" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7zr failed to unpack 7z2602-extra.7z (exit $LASTEXITCODE)." }
    $extracted = Get-ChildItem $extraDir -Recurse -Filter '7za.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $extracted) { throw '7za.exe not found inside the downloaded extra package.' }
    Copy-Item $extracted.FullName (Join-Path $assets7z '7za.exe') -Force
    Remove-Item $extraDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Back-filled 7za cache: $assets7z"
    $SevenZip = Get-Item (Join-Path $assets7z '7za.exe')
}
$SevenZip = $SevenZip.FullName

function Invoke-NativeChecked {
    param(
        [string]$What,
        [scriptblock]$Script,
        [switch]$AllowFailure
    )
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Script
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $oldEap
    }
    if ($code -ne 0 -and -not $AllowFailure) { throw "$What failed with exit code $code" }
    if ($code -ne 0) { return }
    $output
}

function Copy-Tree([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) { throw "Missing source tree: $Source" }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    robocopy $Source $Destination /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $Source -> $Destination" }
}

function Remove-TreeSafe([string]$Path) {
    # Long-path-safe deletion: plain Remove-Item fails silently on >MAX_PATH
    # trees (e.g. website i18n docs, node_modules), leaving a poisoned stage.
    # A lingering handle (e.g. a shell whose cwd sits inside the tree) makes
    # even cmd rd fail, so retry PowerShell once after a short pause before
    # falling back to the subst trick.
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        Remove-Item $Path -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $Path)) { return }
        if ($attempt -eq 0) { Start-Sleep -Seconds 2 }
    }
    $parent = Split-Path $Path -Parent
    $leaf = Split-Path $Path -Leaf
    $candidates = 'H','G','F','I','J','K','L','M','N'
    foreach ($letter in $candidates) {
        if (Test-Path "${letter}:\") { continue }
        subst "${letter}:" $parent | Out-Null
        try {
            cmd.exe /d /c "rd /s /q ${letter}:\$leaf" | Out-Null
        } finally {
            subst "${letter}:" /d | Out-Null
        }
        break
    }
}

function Get-FreePort {
    # Ask the OS for an unused loopback port (bind port 0). The port is
    # released on return; a racy reuse is unlikely and the probe retries.
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    try {
        $l.Start()
        return ([System.Net.IPEndPoint]$l.LocalEndpoint).Port
    } finally {
        $l.Stop()
    }
}

function Assert-Upstream {
    $officialRepo = 'https://github.com/deepseek-ai/deepseek-harness.git'
    if (-not (Test-Path (Join-Path $Repo '.git'))) {
        throw "Upstream checkout missing at $Repo — run: git clone $officialRepo `"$Repo`""
    }
    $dirty = & git.exe -C $Repo status --porcelain
    if ($dirty) {
        Write-Host "WARN: upstream checkout is not clean (will still build from HEAD):"
        $dirty | Select-Object -First 3
    }
    $commit = (& git.exe -C $Repo rev-parse HEAD).Trim()
    Write-Host "Building from upstream commit $commit"
    return $commit
}

# Node version is pinned to keep builds reproducible: the release must ship
# the exact version the launcher/README advertise, and every candidate must
# satisfy dsh engines.node (^22.19.0 || >=24.0.0). 22.23.2 is that pin.
$NodeVersion = '22.23.2'

# pnpm is pinned for the same reason as Node: the builder installs exactly
# this version (bundled npm, never the system pnpm) and caches it under
# builder\assets\pnpm. Must stay v11 — --config.dangerously-allow-all-builds
# is pnpm-11 syntax and older majors silently ignore it.
$PnpmVersion = '11.21.0'

function Resolve-Node {
    # Portable Node for the release. Use this builder's own cached zip
    # (builder\assets\node\node-v22.23.2-win-x64.zip), else download the
    # pinned version from nodejs.org and back-fill the cache. Never resolve
    # "latest" — a future v22.x would silently change the shipped runtime.
    $nodeZip = Get-ChildItem (Join-Path $Builder "assets\node") -File -Filter "node-v$NodeVersion-win-x64.zip" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $nodeZip) {
        # The pinned version has a stable URL under /dist/v<version>/; build it
        # directly instead of scraping the "latest" index (which would only
        # ever surface the newest v22, not our pin). Back-fill the assets
        # cache so later builds are offline.
        $url = "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-win-x64.zip"
        $dest = Join-Path $env:TEMP "node-v$NodeVersion-win-x64.zip"
        Write-Host "Downloading node-v$NodeVersion-win-x64.zip..."
        Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing -TimeoutSec 600
        $nodeCacheDir = Join-Path $Builder "assets\node"
        New-Item -ItemType Directory -Force $nodeCacheDir | Out-Null
        Copy-Item $dest (Join-Path $nodeCacheDir "node-v$NodeVersion-win-x64.zip") -Force
        Write-Host "Back-filled node cache: $nodeCacheDir"
        $nodeZip = Get-Item $dest
    }
    $extract = Join-Path $Stage 'node'
    New-Item -ItemType Directory -Force $extract | Out-Null
    Write-Host "Extracting $($nodeZip.Name) -> $extract"
    & $SevenZip x $nodeZip.FullName "-o$extract" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Node zip extraction failed.' }
    # node zip contains a single top-level dir node-vX.Y.Z-win-x64; hoist it.
    $inner = Get-ChildItem $extract -Directory | Select-Object -First 1
    if ($inner -and $inner.Name -ne 'node') {
        $hoisted = Join-Path $Stage 'node-hoist'
        New-Item -ItemType Directory -Force $hoisted | Out-Null
        robocopy $inner.FullName $hoisted /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $($inner.FullName) -> $hoisted" }
        Remove-TreeSafe $extract
        Move-Item $hoisted $extract
    }
    $nodeExe = Join-Path $extract 'node.exe'
    if (-not (Test-Path $nodeExe)) { throw "node.exe missing after extraction: $nodeExe" }
    return $extract
}

function Resolve-Pnpm {
    param([string]$NodeDir)
    $npmCmd = Join-Path $NodeDir 'npm.cmd'
    if (-not (Test-Path $npmCmd)) { throw "npm.cmd missing in $NodeDir" }
    $pnpmCacheDir = Join-Path $Builder 'assets\pnpm'
    # The builder never uses a system pnpm — everything comes from its own
    # assets (like node/7za). Resolution order: (1) this builder's cached
    # pnpm under builder\assets\pnpm (offline); (2) fresh install of the
    # pinned version with the bundled npm, then back-fill the cache.
    # Cached install: pnpm 11 ships as one self-contained package dir (all
    # deps bundled inside node_modules\pnpm — verified 2026-08-19), so copying
    # the cached package dir + shims back into the node dir is fully offline.
    $cachedPkg = Join-Path $pnpmCacheDir 'pnpm'
    $cachedCmd = Join-Path $pnpmCacheDir 'pnpm.cmd'
    if ((Test-Path $cachedPkg) -and (Test-Path $cachedCmd)) {
        Write-Host "Installing pnpm@$PnpmVersion from builder cache ($pnpmCacheDir)..."
        if (Test-Path (Join-Path $NodeDir 'node_modules\pnpm')) { Remove-Item (Join-Path $NodeDir 'node_modules\pnpm') -Recurse -Force -ErrorAction SilentlyContinue }
        Copy-Item $cachedPkg (Join-Path $NodeDir 'node_modules\pnpm') -Recurse -Force
        Copy-Item $cachedCmd $NodeDir -Force
        foreach ($shim in @('pnpm.ps1', 'pnpm', 'pnpx.cmd', 'pnpx.ps1', 'pnpx')) {
            $src = Join-Path $pnpmCacheDir $shim
            if (Test-Path $src) { Copy-Item $src $NodeDir -Force }
        }
        $globalBin = Join-Path $NodeDir 'pnpm.cmd'
        if (-not (Test-Path $globalBin)) { throw 'pnpm.cmd could not be restored from cache.' }
        return $globalBin
    }
    # Fresh install with the bundled npm, then back-fill the cache so later
    # builds (and offline builders) never need the network again.
    Write-Host "Installing pnpm@$PnpmVersion with the bundled npm (first run; cached afterwards)..."
    & $npmCmd install -g "pnpm@$PnpmVersion" --no-fund --no-audit --silent | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "npm install -g pnpm@$PnpmVersion failed (exit $LASTEXITCODE)." }
    $globalBin = Join-Path $NodeDir 'pnpm.cmd'
    if (-not (Test-Path $globalBin)) { throw 'pnpm.cmd could not be resolved after global install.' }
    New-Item -ItemType Directory -Force $pnpmCacheDir | Out-Null
    Copy-Item (Join-Path $NodeDir 'node_modules\pnpm') $pnpmCacheDir -Recurse -Force
    Copy-Item $globalBin $pnpmCacheDir -Force
    foreach ($shim in @('pnpm.ps1', 'pnpm', 'pnpx.cmd', 'pnpx.ps1', 'pnpx')) {
        $src = Join-Path $NodeDir $shim
        if (Test-Path $src) { Copy-Item $src $pnpmCacheDir -Force }
    }
    Write-Host "Back-filled pnpm cache: $pnpmCacheDir"
    return $globalBin
}

function Build-DshPackage {
    param([string]$WorkDir, [string]$PnpmPath)
    # Install the published @deepseek-ai/dsh package with node-linker=hoisted:
    # produces a flat, symlink-free node_modules that survives archive/restore
    # (verified 2026-08-18 — plain pnpm symlink store breaks after 7za roundtrip).
    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    Copy-Item (Join-Path $Templates 'package.json') (Join-Path $WorkDir 'package.json') -Force
    Set-Content -Path (Join-Path $WorkDir '.npmrc') -Value 'node-linker=hoisted' -Encoding ASCII
    $version = (Get-Content (Join-Path $Repo 'package.json') -Raw | ConvertFrom-Json).version
    $oldPath = $env:PATH
    Push-Location $WorkDir
    try {
        $env:PATH = "$(Join-Path $Stage 'node');$env:PATH"
        Write-Host "Installing @deepseek-ai/dsh@$version (node-linker=hoisted)..."
        Invoke-NativeChecked "pnpm add @deepseek-ai/dsh@$version (hoisted)" {
            # Pin the official registry explicitly: a user-level npm/pnpm config
            # pointing at a mirror (e.g. npmmirror) makes the cross-platform
            # optional deps (ripgrep/koffi/sharp per-OS tarballs) fail with
            # UND_ERR_DESTROYED. Direct npmjs.org was measured ~0.7s here.
            # dangerously-allow-all-builds is required: pnpm 11 blocks dep
            # install scripts by default and exits 1 with
            # ERR_PNPM_IGNORED_BUILDS (node-pty/koffi native modules need them).
            & $PnpmPath add "@deepseek-ai/dsh@$version" --config.node-linker=hoisted --registry=https://registry.npmjs.org/ --config.dangerously-allow-all-builds
        }
    } finally {
        Pop-Location
        $env:PATH = $oldPath
    }
    $bin = Join-Path $WorkDir 'node_modules\.bin\dsh.cmd'
    if (-not (Test-Path $bin)) { throw "dsh.cmd missing after install: $bin" }
}

# ---------------------------------------------------------------- main
$commit = Assert-Upstream

# Delete staged portable before any build work.
Remove-TreeSafe $Stage
if (Test-Path $Stage) {
    throw "Staging tree could not be fully removed at script start: $Stage (a process is holding files?). Close such processes and retry."
}
New-Item -ItemType Directory -Force $Stage | Out-Null

Write-Host 'Resolving portable Node runtime...'
$nodeDir = Resolve-Node
$pnpm = Resolve-Pnpm $nodeDir
Write-Host "Node runtime ready: $nodeDir"
Write-Host "pnpm: $pnpm"

# Build the dsh package into a staging workdir (hoisted, flat node_modules).
$appDir = Join-Path $Stage 'app'
Build-DshPackage $appDir $pnpm

# Assemble the portable tree.
$dataDir = Join-Path $Stage 'data'
New-Item -ItemType Directory -Force (Join-Path $dataDir 'dsh') | Out-Null

# Bundle every builder-managed preinstall. builder\data mirrors the deployed
# data layout (data\dsh\skills\..., data\dsh\profiles\...), copied as-is —
# adding any file under builder\data is enough to ship it. (Same contract as
# the Hermes builder.)
$builderData = Join-Path $Builder 'data'
if (Test-Path $builderData) {
    Copy-Tree $builderData (Join-Path $Stage 'data')
    Write-Host "Preinstalled builder data: $builderData"
}

# Launcher + README.
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
$launcherIcon = Join-Path $Templates 'DeepSeek-Harness.ico'
if (-not (Test-Path $launcherIcon)) { throw "Launcher icon missing: $launcherIcon" }
Invoke-NativeChecked 'DeepSeek-Harness launcher compilation' {
    & $csc /nologo /target:winexe /platform:anycpu /optimize+ "/win32icon:$launcherIcon" "/out:$Stage\DeepSeek-Harness.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $Templates 'DeepSeek-Harness.cs')
}
if (-not (Test-Path (Join-Path $Stage 'DeepSeek-Harness.exe'))) { throw 'DeepSeek-Harness.exe was not produced.' }

Copy-Item (Join-Path $Templates 'README.txt') (Join-Path $Stage 'README.txt') -Force

# Ship the in-place updater as a windowless winexe at the portable root
# (double-clickable; uses the bundled npm, never touches data\dsh).
$updateIcon = Join-Path $Templates 'DeepSeek-Harness.ico'
Invoke-NativeChecked 'Update.exe compilation' {
    & $csc /nologo /target:winexe /platform:anycpu /optimize+ "/win32icon:$updateIcon" "/out:$Stage\Update.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $Templates 'Update.cs')
}
if (-not (Test-Path (Join-Path $Stage 'Update.exe'))) { throw 'Update.exe was not produced.' }

$version = (Get-Content (Join-Path $Repo 'package.json') -Raw | ConvertFrom-Json).version
$nodeVersion = (& (Join-Path $Stage 'node\node.exe') --version).Trim()
$readme = [IO.File]::ReadAllText((Join-Path $Templates 'README.txt'), [Text.Encoding]::UTF8)
$readme = $readme.Replace('{{DEEPSEEK_HARNESS_VERSION}}', $version).Replace('{{SOURCE_COMMIT}}', $commit).Replace('{{NODE_VERSION}}', $nodeVersion)
[IO.File]::WriteAllText((Join-Path $Stage 'README.txt'), $readme, [Text.UTF8Encoding]::new($false))

# Verify the staged dsh runs. Run node against the real entry (lib\bin.js)
# — dsh.cmd is a cmd shim and node.exe cannot be handed a .cmd file.
Write-Host 'Verifying staged dsh...'
$dshEntry = Join-Path $Stage 'app\node_modules\@deepseek-ai\dsh\lib\bin.js'
if (-not (Test-Path $dshEntry)) { throw "dsh entry not found: $dshEntry" }
$nodeExe = Join-Path $Stage 'node\node.exe'
$oldDshHome = $env:DSH_HOME
$env:DSH_HOME = Join-Path $Stage 'data\dsh'
try {
    $dshVersion = Invoke-NativeChecked 'dsh --version' { & $nodeExe $dshEntry --version }
    if (-not $dshVersion) { throw 'dsh --version returned nothing.' }
    Write-Host "dsh version: $dshVersion"
} finally {
    $env:DSH_HOME = $oldDshHome
}

# Boot the web UI briefly and probe it — proves the packaged tree (node +
# hoisted app + DSH_HOME redirect) actually serves, not just that the bin
# resolves. A free port is picked dynamically (a fixed port could already be
# taken on the build machine). Run node against the real entry (lib\bin.js) —
# dsh.cmd is a cmd shim and cannot be handed to node.exe.
$probePort = Get-FreePort
$oldDshHome = $env:DSH_HOME
$env:DSH_HOME = Join-Path $Stage 'data\dsh'
$probe = Start-Process -FilePath $nodeExe `
    -ArgumentList @($dshEntry, 'web', '--port', "$probePort") `
    -WorkingDirectory (Join-Path $Stage 'app') -PassThru -WindowStyle Hidden
try {
    $ok = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        if ($probe.HasExited) { break }
        try {
            $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$probePort/" -UseBasicParsing -TimeoutSec 3
            if ($resp.StatusCode -eq 200) { $ok = $true; break }
        } catch { }
    }
    if (-not $ok) { throw "dsh web probe failed (port $probePort)." }
    Write-Host "dsh web probe OK (HTTP 200 on port $probePort)."
} finally {
    $env:DSH_HOME = $oldDshHome
    if (-not $probe.HasExited) {
        # Kill the whole tree: dsh web may spawn child processes, and a
        # surviving node would hold stage files and break the archive step.
        # taskkill /T is the reliable tree kill on Windows.
        & taskkill.exe /PID $probe.Id /T /F 2>$null | Out-Null
        Start-Sleep -Milliseconds 500
    }
}

# Archive (unless skipped).
if (-not $SkipArchive) {
    New-Item -ItemType Directory -Force $Dist | Out-Null

    # The probe boot generated $Stage\data\dsh\profiles (with a node_modules
    # symlink farm) and storages\workspace.json. None of it may ship: the
    # launcher self-heals on every start (deletes the farm + lets dsh
    # rebuild), and 7za follows the junctions into the archive. MUST run
    # BEFORE archiving. Only probe-generated entries are removed — preinstalled
    # builder\data content (data\dsh\skills, and profiles\web\cordis.patch.yml
    # etc.) is preserved. Remove-Item -Recurse is unreliable on junction
    # trees, so use cmd rd via subst as fallback.
    $dshData = Join-Path $Stage 'data\dsh'
    if (Test-Path $dshData) {
        foreach ($child in (Get-ChildItem $dshData -Force -ErrorAction SilentlyContinue)) {
            # Whitelist probe artifacts only; keep preinstalled content.
            if ($child.Name -notin @('profiles', 'storages')) { continue }
            if ($child.Name -eq 'profiles') {
                # profiles is a hybrid: preinstalled web\ (cordis.patch.yml)
                # must survive, only probe-generated entries are junk.
                foreach ($sub in (Get-ChildItem $child.FullName -Force -ErrorAction SilentlyContinue)) {
                    if ($sub.Name -eq 'web') { continue }
                    Remove-Item $sub.FullName -Recurse -Force -ErrorAction SilentlyContinue
                    if (Test-Path $sub.FullName) {
                        # Junction tree survived Remove-Item; nuke via subst.
                        $parent = Split-Path $sub.FullName -Parent
                        $leaf = Split-Path $sub.FullName -Leaf
                        foreach ($letter in 'H','G','F','I','J') {
                            if (Test-Path "${letter}:\") { continue }
                            subst "${letter}:" $parent | Out-Null
                            try { cmd.exe /d /c "rd /s /q ${letter}:\$leaf" | Out-Null } finally { subst "${letter}:" /d | Out-Null }
                            break
                        }
                    }
                }
                continue
            }
            Remove-Item $child.FullName -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path $child.FullName) {
                # Junction tree survived Remove-Item; nuke via subst.
                $parent = Split-Path $child.FullName -Parent
                $leaf = Split-Path $child.FullName -Leaf
                foreach ($letter in 'H','G','F','I','J') {
                    if (Test-Path "${letter}:\") { continue }
                    subst "${letter}:" $parent | Out-Null
                    try { cmd.exe /d /c "rd /s /q ${letter}:\$leaf" | Out-Null } finally { subst "${letter}:" /d | Out-Null }
                    break
                }
            }
        }
        # Any remaining probe artifacts mean the cleanup failed.
        foreach ($child in (Get-ChildItem $dshData -Force -ErrorAction SilentlyContinue)) {
            if ($child.Name -eq 'storages') {
                throw "Probe-generated dsh data could not be removed: $($child.FullName)"
            }
        }
        # profiles may keep only the preinstalled web\ patch layer.
        $profilesDir = Join-Path $dshData 'profiles'
        if (Test-Path $profilesDir) {
            foreach ($sub in (Get-ChildItem $profilesDir -Force -ErrorAction SilentlyContinue)) {
                if ($sub.Name -ne 'web') {
                    throw "Probe-generated profiles entry could not be removed: $($sub.FullName)"
                }
            }
        }
        Write-Host 'Cleared probe-generated dsh data (profiles farm/storages); kept preinstalled skills and profile patches.'
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $archive = Join-Path $Dist "DeepSeek-Harness-Portable-$version-win-x64-$stamp.zip"
    Write-Host "Creating archive: $archive"
    # 7za writes progress to stderr; under $ErrorActionPreference='Stop' that
    # surfaces as NativeCommandError even on success. Run with EAP relaxed and
    # judge purely by $LASTEXITCODE (same contract as Invoke-NativeChecked).
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # Archive the stage directory itself: $Stage IS the portable root, and
        # the zip must contain a top-level folder on extraction.
        & $SevenZip a -tzip $archive $Stage -y | Out-Null
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $oldEap
    }
    if ($code -ne 0) { throw "Archive creation failed (exit $code)." }
    # Verify the archive is complete before declaring success (a killed 7za
    # leaves a truncated zip that passes no one).
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $SevenZip t $archive | Out-Null
        $testCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $oldEap
    }
    if ($testCode -ne 0) { throw "Archive integrity check failed (exit $testCode)." }
    Write-Host "Portable release built and verified: $archive"
}
Write-Host 'Build complete.'
