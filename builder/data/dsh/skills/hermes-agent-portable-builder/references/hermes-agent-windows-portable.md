# Hermes Agent Desktop on Windows: portable packaging notes

Source audit baseline:

- Repository: `NousResearch/hermes-agent`
- Branch: `main`
- Audited commit: `76173ba8cd18ef10b0cf9366346d9e794e55ce94`
- Date audited: 2026-07-29

Treat this as a point-in-time reference. Re-read current source before acting.

## Contents (consolidated 2026-08-10)

Main body: Hermes Agent Desktop Windows portable packaging notes.
Appendices (merged from the former one-file-per-topic layout):

- Appendix: Desktop patch line endings
- Appendix: Electron first-run defaults
- Appendix: Portable builder web-dist fix
- Appendix: Portable install diagnosis
- Appendix: uv-managed Python portable updates
- Appendix: Windows managed-python dedup
- Appendix: Windows csc compilation
- Appendix: Windows console output encoding
- Appendix: Windows PowerShell build execution
- Appendix: Windows PowerShell native commands
- Appendix: Update.exe error dialog
- Appendix: Packaged .git conversion (2026-09-02)
- Appendix: hermes update failure classes (2026-09-02)
- Appendix: Prebuilt web/TUI bundles & build stamps (2026-09-02)

## Upstream build

Install workspace dependencies from the repository root; the Desktop scripts explicitly check the root workspace installation.

```powershell
npm ci
Set-Location apps/desktop
npm run typecheck
npm run test:ui
npm run test:desktop:platforms
npm run pack       # unpacked output
npm run dist:win   # NSIS + MSI
```

Explicit unpacked x64 build:

```powershell
npm run build
npm run builder -- --win --x64 --dir
```

Output is under `apps/desktop/release/`, normally `release/win-unpacked/`.

Desktop requires Node `^20.19.0 || >=22.12.0`. At the audited commit it uses Electron `40.10.2`, electron-builder `^26.8.1`, and `node-pty 1.1.0`.

## What the artifact contains

The normal Electron package does **not** include the Python backend. `scripts/before-build.mjs` returns `false` to skip electron-builder dependency collection and explicitly states that the Python payload is installed on first launch through the platform installer.

Consequences:

- `npm run pack` is not a clean-machine offline portable distribution.
- `npm run dist:win` creates installer formats, but first launch may still bootstrap the Hermes runtime.
- A genuine offline portable build must add Python, Hermes source/dependencies, and writable state routing.

## Packaging hooks

### Build

`npm run build` performs:

1. cleanup
2. `write-build-stamp.mjs`
3. Vite renderer build
4. Electron main/preload bundling
5. `stage-native-deps.mjs` for `node-pty`
6. post-build artifact checks

The install stamp is shipped through `extraResources`. CI Git SHA wins, then local Git HEAD, then an all-zero fallback commit that causes first-launch bootstrap to follow a branch.

### beforePack

- Cleans stale unpacked output left by interrupted builds.
- On Windows, preserves a previous working unpacked tree as `.bak` for rollback.
- Restages `node-pty` using electron-builder's actual target platform and architecture.
- Fails packaging when native staging is invalid.

### afterPack

On Windows, best-effort `rcedit` stamps icon/product identity onto `Hermes.exe`. Failure is cosmetic and does not fail packaging.

### Builder wrapper

`run-electron-builder.mjs` resolves hoisted workspace packages with `require.resolve()`. If the locally installed Electron dist contains `electron.exe`, it passes `-c.electronDist=<dist>`; otherwise electron-builder downloads through `@electron/get` and may honor `ELECTRON_MIRROR`.

## HERMES_HOME resolution

Electron main process precedence:

1. process `HERMES_HOME`
2. `HERMES_DESKTOP_USER_DATA_DIR` sandbox → `<override>/hermes-home`
3. Windows user registry `HERMES_HOME`
4. `%LOCALAPPDATA%\hermes`
5. legacy `%USERPROFILE%\.hermes` when it exists and the LocalAppData home does not
6. `~/.hermes` on non-Windows

Derived paths include:

```text
ACTIVE_HERMES_ROOT = HERMES_HOME\hermes-agent
VENV_ROOT          = HERMES_HOME\hermes-agent\venv
desktop log        = HERMES_HOME\logs\desktop.log
bootstrap marker   = HERMES_HOME\hermes-agent\.hermes-bootstrap-complete
```

The child process receives an explicit `HERMES_HOME` so Electron and Python do not split state between `%LOCALAPPDATA%\hermes` and `%USERPROFILE%\.hermes`.

`normalizeHermesHomeRoot()` strips a trailing `profiles/<name>` shape back to the base home; profile selection is passed to Python using `--profile`.

## Backend resolver

Audited order in `resolveHermesBackend()`:

1. `HERMES_DESKTOP_HERMES_ROOT`
2. 开发源码
3. managed install at `HERMES_HOME\hermes-agent`
4. `HERMES_DESKTOP_HERMES` or `hermes` on PATH
5. system Python that can import `hermes_cli`
6. first-launch bootstrap through `install.ps1`

Python-backed command:

```text
python.exe -m hermes_cli.main [--profile NAME] serve --host 127.0.0.1 --port 0
```

An older runtime without `serve` is detected and rewritten to:

```text
dashboard --no-open --host 127.0.0.1 --port 0
```

Windows managed installs prefer `venv\Scripts\python.exe`; the spawn uses hidden-window options and keeps stdout for readiness. Environment construction also covers `PYTHONPATH`, `PYTHONUTF8=1`, managed Node and venv entries on PATH, `TERMINAL_CWD`, desktop/session markers, and web-dist paths.

## First-launch bootstrap

Electron's bootstrap runner invokes PowerShell with:

```text
powershell.exe -NoProfile -ExecutionPolicy Bypass -File install.ps1 ...
```

At the audit point, the installer defaults to Python 3.11 with fallbacks, creates the venv inside `HERMES_HOME\hermes-agent`, installs dependencies with uv, and can install managed PortableGit and Node beneath `HERMES_HOME`.

This is an online managed installation, not evidence that those payloads are inside the Electron artifact.

## Minimal portable conversion

Recommended directory layout:

```text
Hermes-Portable/
├─ Hermes.exe
├─ resources/
│  ├─ app.asar
│  ├─ backend/hermes-agent/
│  └─ python/python.exe
├─ data/
│  ├─ hermes-home/
│  └─ electron-user-data/
└─ portable launcher
```

Existing upstream overrides useful for a prototype launcher:

```bat
set "HERMES_HOME=%ROOT%data\hermes-home"
set "HERMES_DESKTOP_USER_DATA_DIR=%ROOT%data\electron-user-data"
set "HERMES_DESKTOP_HERMES_ROOT=%ROOT%resources\backend\hermes-agent"
set "HERMES_DESKTOP_PYTHON=%ROOT%resources\python\python.exe"
```

For direct double-click support, derive these paths in Electron before `USER_DATA_OVERRIDE`, `app.setPath('userData', ...)`, and `HERMES_HOME` constants are initialized. Add runtime/backend trees through `extraResources` or a post-pack staging step.

Prefer an unpacked-directory ZIP over electron-builder's single-file portable target. A single-file target may self-extract under a temporary directory, so packaged resource paths are not a stable writable root.

## Verified Windows x64 directory-ZIP pattern

A working layout used separate immutable-ish application/runtime payloads and mutable data:

```text
Hermes-Agent-Portable-win-x64/
├─ app/
│  ├─ Hermes.exe
│  ├─ portable.marker
│  └─ resources/...
├─ runtime/
│  ├─ bin/uv.exe
│  ├─ bin/hermes-cli.cmd
│  ├─ bin/hermes-tui.cmd
│  ├─ bin/hermes-dashboard.cmd
│  └─ python/<exact-runtime-from-current.txt>/python.exe
├─ scripts/
│  ├─ Update-Portable.ps1
│  ├─ Repair-Portable.ps1
│  └─ Verify-Portable.ps1
├─ data/
│  ├─ electron-user-data/
│  └─ hermes-home/
│     ├─ hermes-agent/
│     │  └─ venv/Lib/site-packages/
│     ├─ node/
│     └─ git/
├─ Hermes.exe
├─ Update.exe
└─ README.txt
```

The Electron main process detects `portable.marker` beside `Hermes.exe` before deriving `USER_DATA_OVERRIDE`, then sets defaults only when the caller has not already supplied them:

```text
HERMES_HOME=<root>\data\hermes-home
HERMES_DESKTOP_USER_DATA_DIR=<root>\data\electron-user-data
HERMES_DESKTOP_HERMES=<root>\runtime\bin\hermes-cli.cmd
HERMES_GIT_BASH_PATH=<root>\data\hermes-home\git\bin\bash.exe
UV_PYTHON_INSTALL_DIR=<root>\runtime\python
```

It prepends the embedded venv scripts, Node, PortableGit directories, and runtime bin directory to `PATH`. Explicit environment values retain precedence.

### Relative Python wrapper

The reliable wrapper derives paths on every invocation and reads only the updater-maintained exact runtime pointer:

```bat
@echo off
setlocal
set "ROOT=%~dp0..\.."
set "HERMES_HOME=%ROOT%\data\hermes-home"
set "AGENT_ROOT=%HERMES_HOME%\hermes-agent"
set "HERMES_PORTABLE_SITE_PACKAGES=%AGENT_ROOT%\venv\Lib\site-packages"
set "PYTHONPATH=%ROOT%/runtime/python-bootstrap;%AGENT_ROOT%;%PYTHONPATH%"
set "PYTHON_DIR="
if exist "%ROOT%/runtime/python/current.txt" set /p "PYTHON_DIR="<"%ROOT%/runtime/python/current.txt"
if not defined PYTHON_DIR exit /b 1
set "PYTHON=%ROOT%/runtime/python/%PYTHON_DIR%/python.exe"
if not exist "%PYTHON%" exit /b 1
"%PYTHON%" -m hermes_cli.main %*
```

Keep the retained venv's `site-packages` as the dependency payload, but bypass its generated `python.exe` and console-script launchers at runtime. The `python-bootstrap` directory contains `sitecustomize.py`, which calls `site.addsitedir(HERMES_PORTABLE_SITE_PACKAGES)`. Direct `PYTHONPATH` injection alone does not process `.pth` files; on Windows that skips pywin32 path and DLL setup and makes MCP stdio fail with a misleading MCP-SDK-unavailable message.

### Verified toolchain payload

The complete x64 bundle included:

- Electron Desktop unpacked build
- CPython selected from the current official Windows installer `scripts/install.ps1` `$PythonVersion` and provisioned under `runtime/python`
- Hermes 源码 and locked Python dependencies
- Node.js 22 x64 under `HERMES_HOME/node`
- PortableGit x64 under `HERMES_HOME/git`
- managed `uv.exe`

Build-only `node_modules` and stale Electron `release/dist` trees inside the 内嵌源码 can be removed after packaging to reduce archive size. **Do not delete Git-tracked source files** (including `apps/desktop`, docs, tests, or bootstrap sources): their absence is recorded as local deletion, so `hermes update` autostashes thousands of paths and asks whether to restore them. Retain the complete 被跟踪的源码 plus runtime Python, dependency payload, and application artifact. Before archiving, require:

```powershell
git status --porcelain   # no output
git stash list           # no output
git config core.longpaths true
git config core.autocrlf false
git config core.eol lf
```

When a 深层 Windows 源码 cannot restore tracked files due to `Filename too long`, temporarily map the 源码 with `subst`, run `git reset --hard HEAD` through the short drive, then remove the mapping and re-check status.

## Updater compatibility: launch portability is not update portability

The relative launcher wrapper solves runtime launch after moving the root, but upstream `hermes update` may use a different install path. At the July 2026 baseline, the updater exports:

```text
VIRTUAL_ENV=<checkout>\venv
```

and invokes managed uv as `uv pip install ...`. If the retained Windows venv's `pyvenv.cfg` or trampoline still names the build/extraction path, uv fails with:

```text
Failed to inspect Python interpreter from active virtual environment
Broken Python trampoline at venv\Scripts\python.exe
```

The Git pull can already have advanced `HEAD`, and the ZIP fallback can already have copied source files, while dependency installation remains incomplete. The 源码 may contain `.update-incomplete`; subsequent launches then retry repair and log `Could not auto-recover`. Meanwhile Desktop can appear functional because the relative wrapper directly launches bundled CPython with retained `site-packages`. Treat that state as **source refreshed, runtime update incomplete**.

A Portable updater must therefore:

1. require Desktop and every backend process beneath the portable root to exit;
2. snapshot `data/` and record the current commit/version;
3. avoid passing a broken retained venv through `VIRTUAL_ENV`;
4. probe imports and recreate the venv with bundled CPython + locked dependencies when unhealthy, retaining rollback until verification passes;
5. remove the marker-bounded generated Portable patch before upstream update and require a truly 干净源码 (`git status --porcelain` and `git stash list` both empty); do not answer an autostash restore prompt as the normal fix;
6. run the official source update only after that target is healthy and distinguish network failure from runtime repair failure;
7. reapply the same patch idempotently after the update using explicit UTF-8 decoding (PowerShell 5.1 `Get-Content` can corrupt non-ASCII source comments); `-Remove` must be a success when the patch/source is already absent;
8. build `apps/desktop/release/win-unpacked`, validate `Hermes.exe`, and atomically synchronize it into the external Portable `app/` directory with rollback;
9. verify the final update log reaches success rather than stopping after Git pull/ZIP extraction;
10. confirm `.update-incomplete` is absent, core imports pass, `--version` reports the expected commit/version, and direct `app/Hermes.exe` launch reaches backend readiness;
11. remove stale venv rollback and only ignored build caches such as source `node_modules`, `release`, and `dist` after successful cutover, using a long-path-safe deletion fallback;
12. remove the generated source patch again before producing the next clean archive, then repeat from a renamed/moved root.

### Verified updater repair/build pattern

Use separate helpers for venv preflight/rebuild, idempotent Portable Electron source patching, official Desktop build plus external `app/` atomic swap, and the top-level update orchestrator.

The venv probe must temporarily tolerate native stderr and convert it to `healthy = false`; otherwise Windows PowerShell can terminate before reaching the repair branch. For deep-tree cleanup, try normal removal, then map the parent to a temporary drive and remove the shortened path. Cleanup failure after a successful rebuild must not roll back a healthy new venv.

After updating source, do not assume upstream's own Desktop rebuild runs: its gate may only detect `apps/desktop/dist` or `apps/desktop/release`, while a directory Portable launches `<portable-root>/app/Hermes.exe`. Explicitly build and sync that external app tree.

Do not consider the update path fully verified until a real upstream update or controlled repair scenario completes this sequence. `hermes update --check` alone is insufficient.

## Fresh-release state and onboarding

A Portable release must distinguish runtime payload from user state. Do not archive a staging tree after launching it unless writable state is sanitized again.

For a fresh Hermes Desktop ZIP, keep `data/electron-user-data/` empty. Under `data/hermes-home/`, retain the complete 官方 `hermes-agent` 源码, bundled `node`, bundled `git`, managed `bin` tools; the archive ships WITHOUT `config.yaml` — the stage is assembled from an unlaunched upstream 源码 so the file never exists in the normal flow, and the build fail-closes around it (early defensive `$ConfigPath` remove + throw in `Hermes.ps1`, plus the contract gate's final absence check; verified 2026-08-13). The first-run launcher writes the minimal `config.yaml` (`display.language: zh`) only when the file is absent (`Hermes.cs::EnsureFirstRunConfig` `FileMode.CreateNew`), so user config is never overwritten. Exclude `.env`, `auth.json`, `state.db*`, sessions, logs, memories, caches, state snapshots, profiles, pairing state, and other test/user artifacts.

Hermes Desktop persists onboarding decisions in Electron localStorage. At the July 2026 source baseline the relevant keys are:

```text
hermes-desktop-onboarded-v1
hermes-onboarding-skipped-v1
```

If either arrives via `Local Storage/leveldb`, a newly extracted package can skip the provider onboarding overlay even though upstream still ships it. Verify fresh onboarding only from an empty writable data root (launch a disposable extraction with `--remote-debugging-port=<PORT>`, evaluate `document.body.innerText` via CDP, assert expected localized copy, then stop/delete the fixture and sanitize the packaging tree before archiving).

## End-to-end acceptance sequence

The full sequence (Desktop typecheck/lint → win-unpacked build with native node-pty staged for win32-x64 → component verifier for Hermes/Python/Node/Git/uv → launch and confirm one embedded Python backend owns a loopback port → move the root and repeat → stop processes and exercise update/repair from the moved root → ZIP → extract into a fresh differently named directory and repeat readiness → exercise the shipped updater with a real delta or fixture → regenerate checksum after the final rebuild) is captured in the main skill's Verification section — follow that. One note unique to this audit: a full platform test suite may contain upstream POSIX fixture failures on Windows (for example, tests expecting `/venv/lib/pythonX.Y/site-packages`). Report those honestly, then run focused Windows/backend tests; do not convert a partial suite failure into a blanket pass.

## External Builder-owned release templates and contract gate

`D:\Hermes-Agent-Portable-Builder\builder` carries these inputs outside the 官方源码. The build installs the non-official skill at `<portable-root>\data\hermes-home\skills\software-development\hermes-agent-portable-builder`; official source remains under `upstream` / `data\hermes-home\hermes-agent` with no Portable commits:

- `source/hermes-cli.cmd` — pointer-only CLI launcher; never hard-codes a CPython patch directory.
- `scripts/Update-Portable.ps1` — the merged patch/update helper (single script, `-Stage Patch|PatchRemove|SyncDesktop|WriteDesktopStamp`): the Patch/PatchRemove stages apply/remove the marker-bounded Desktop source patch (build-time `-RepoPath`, deployed `-PortableRoot`); the SyncDesktop stage rebuilds Desktop/TUI/Web and atomically swaps `app`; `WriteDesktopStamp` records the pristine desktop content hash on the build machine. Python provisioning — parsing the embedded 源码的 current `scripts/install.ps1` `$PythonVersion`, passing it to `uv python install/find`, validating major/minor, rebuilding the venv, and updating `current.txt` transactionally — lives in the standalone `scripts/Repair-Portable.ps1` (`-UpdatePython`), which became the self-contained repair entry when the `Repair` stage was split out of `Update-Portable.ps1` (2026-08-10).
- `source/README.txt` — release README template; the public CLI path is `runtime\bin\hermes-cli.cmd` and there is no root `Hermes-CLI.exe`.
- `Hermes.ps1` embeds the two release gates as in-script functions (merged into the build script 2026-08-10): `Test-PortablePythonContract` rejects a mismatched Python selector/runtime, hard-coded launcher directory, obsolete root CLI executable, or stale README path, and enforces the MCP import regression probe; `Test-PortableNoEditableInstall` rejects non-relocatable editable metadata (`__editable__*`, `*.egg-link`) and build-root absolute paths in `.pth` files.

At assembly time (Hermes.ps1), render the README metadata placeholders from the actual 源码 and runtime probes, copy these templates into the staged tree, omit `Hermes-CLI.exe`, then run the two contract gates (now in-process):

```powershell
Invoke-NativeChecked 'Portable Python contract test' { Test-PortablePythonContract $Stage | Out-Host }
Invoke-NativeChecked 'Portable non-editable install contract test' { Test-PortableNoEditableInstall $Stage | Out-Host }
```

Do not archive unless both gates succeed.

## Critical pitfalls

The full, maintained pitfall list lives in the main skill's Pitfalls section. The audit-level summary:

- Do not treat `pack` success as backend bundling proof.
- Redirect both Hermes state and Electron `userData`.
- Do not write mutable data under `resources` or ASAR.
- Normal Python venvs and editable installs may embed absolute build paths.
- Invoke bundled Python directly with `-m hermes_cli.main`; avoid relying on generated console-script shims.
- Ensure the embedded source/update path is not treated as a mutable 自我更新源码.
- Explicitly target and validate Windows architecture because `node-pty` is native.
- Test after moving the directory, and from space/Unicode/deep paths.
- Verify no state appears in `%LOCALAPPDATA%\hermes`, `%APPDATA%`, or the legacy home.

---

# Appendix: Desktop patch line endings (merged from apply-desktop-patch-line-endings.md)

## Patch stage CRLF 误报因果链与修复（2026-08-03 排障，方案 A 已实现并验证）

相关逻辑位于 `builder/scripts/Update-Portable.ps1 -Stage Patch`。

### 症状与诊断

- `upstream/` 的 `git status` 显示 3 个文件 modified：`apps/desktop/electron/main.ts`, `zoom.ts`, `zoom.test.ts`，但 `git diff HEAD --numstat` 为空 → **内容零差异，纯行尾符噪音**。
- 诊断命令（实测输出）：

```bash
git config --show-origin core.autocrlf       # 全局 → true（hermes-home 便携 git）
git check-attr eol text -- apps/desktop/electron/main.ts   # unspecified
# 官方 .gitattributes 只强制 *.sh / Dockerfile 为 LF；.ts 未覆盖 → 完全受 autocrlf 支配
```

### 因果链（要点）

补丁脚本每次读取 3 个文件都**无条件 CRLF→LF** 再写回（`WriteAllText` UTF-8 no BOM）。补丁内容重复应用无害（"already applied" 短路），**但行尾转换不具此性质**——即使内容没变，文件也被重写成 LF；全局 `core.autocrlf=true` 下检出是 CRLF，git 判定"该 CRLF 却是 LF"→ status 误报 modified。现有 `Refresh-PortableGitIndex` 只在 `-Remove` 分支且 `-PortableRoot` 模式下调用，构建脚本用 `-RepoPath` → 必然跳过，救不了场。

### 修复（方案 A，已实现，2026-08-03 用户拍板）

写回时还原文件原始 EOL：

- 新增 `Read-NormalizedText`：读时检测原始 EOL（含 `\r\n` → CRLF，否则 LF），内容归一化 LF；
- 新增 `Write-TextWithOriginalEol`：写回前防御性 `Replace("\r\n","\n")`（杜绝 CRCRLF 双重转换），再按原始 EOL 还原；
- 全部 8 个 `WriteAllText` 写回点换成 `Write-TextWithOriginalEol`；读取点 3 个文件改用 helper，变量名不变 → 后续匹配/插入逻辑零改动。
- 要点：PS here-string 被 PS 归一化为 LF（不随脚本文件 EOL），插入块匹配不受影响；最终文本已无 `\r`，`Replace("\n","\r\n")` 不会产生 `\r\r\n`。

未采用的方案：B（upstream 仓库级 `core.eol=lf` + 全量 checkout——改持久配置、重 clone 需重设）；C（构建 finally 加 `git restore` 三个文件——治标不治本）。

### 通用教训

**行尾归一化不具"重复无害"性质**——任何在 Windows 上改写 git 跟踪文件的脚本，都应采用"读时检测原始 EOL + 写回还原"模式。

### 验证口径

- apply 后 `git diff --stat` 只显示补丁块（无整文件行尾翻转）；`python -c` 统计 `\r\n` 数 = 全文行数、bare LF = 0。
- remove 后 `git status --short` 空 = 字节级还原。
- 打包产物桌面补丁：查 `app\resources\app.asar.unpacked\dist\electron-main.mjs`（electron-builder 解包主进程；app.asar 内搜不到）。

---

# Appendix: Electron first-run defaults (merged from electron-first-run-defaults.md)

## Electron first-run defaults: zoom and onboarding

### Default vs runtime baseline

- Chromium/Electron actual-size baseline is zoom level `0` = `100%`.
- An application may define a different shipped default; inspect its source and tests before describing the default.
- Electron conversion: `level = log(percent / 100) / log(1.2)`.

### Durable implementation pattern

1. Patch the application's default constant only for the Portable build.
2. Update the exact default-value unit-test block.
3. Keep the patch marker-bounded and idempotently removable before upstream updates.
4. Build the packaged Electron app, then restore official source files before archiving.
5. Never overwrite an existing saved user zoom on every launch.
6. If Chromium resets zoom after an early restore, schedule one delayed reassert that re-reads persisted state; do not reapply a hard-coded default.

### Runtime verification

Launch through the real Portable entry point or reproduce all launcher variables, especially Electron `userData` and backend-home overrides. Enable a temporary CDP port and evaluate the application's public preload API, for example:

```js
await window.hermesDesktop.zoom.get()
```

Verify:

- fresh empty `userData` returns the requested percent;
- expected onboarding text is present;
- backend starts from the bundled runtime;
- changing zoom through the public IPC/settings UI writes persisted state;
- restart returns the user's changed value, not the original seed.

Do not infer active zoom solely from `zoom-state.json`: the file may be correct while `webContents.getZoomLevel()` has been reset by Chromium.

### Release hygiene

After runtime verification, stop all processes under the fixture root, empty Electron state, remove backend sessions/databases/logs/caches/credentials, and rebuild the archive from the sanitized staging tree. Preserve only intentional non-secret defaults. Recompute and read back the checksum after the final archive write.

---

# Appendix: Portable builder web-dist fix (merged from portable-builder-web-dist-fix.md)

## Durable fix: Hermes-Agent-Portable-Builder ships a working web dashboard

Companion to `Portable install diagnosis` (below). After the 2026-08-04 diagnosis, the builder project was modified so FUTURE builds ship a working browser dashboard out of the box; deployed copies were synced byte-identically and verified live.

### Design principle

- The desktop app's own spawned backend must keep serving the DESKTOP bundle (its webview injects `window.hermesDesktop`). It spawns via `python -m hermes_cli.main serve` directly (`apps/desktop/electron/main.ts`, never through `hermes-cli.cmd`) — so fixing the launcher only affects standalone/user-invoked dashboards.
- Fix the STANDALONE path: (1) ship the real web bundle, (2) stop the env leak.

### Where the fix lives (all implemented)

1. `builder/source/Hermes.ps1` — web workspace install + build after the TUI bundle step, before the `package-lock.json` restore guard (one guard then covers BOTH installs). CRITICAL: `hermes_cli/web_dist/` is gitignored upstream, so the staged `git clean -fdx` must exclude it: `& git.exe -C $Checkout clean -fdx -e venv/ -e hermes_cli/tui_dist/ -e hermes_cli/web_dist/`.
2. `builder/scripts/Update-Portable.ps1 -Stage SyncDesktop` — same web build step after the TUI try/catch block, same non-fatal contract (on failure `Remove-TreeBestEffort $webDistDir` + warning — a stale bundle risks protocol mismatch, a missing one fails explicitly "Frontend not built"), same `git checkout -- package-lock.json` restore guard.
3. `builder/source/hermes-cli.cmd` — core leak fix: `set "HERMES_WEB_DIST="` after the PATH line. Safe because the desktop app never uses the .cmd; `hermes-tui.cmd` inherits the clearing (fine — TUI needs no web dist).
4. `builder/scripts/Verify-Portable.ps1` — `$Checks.WebDist = Join-Path $HomeDir 'hermes-agent\hermes_cli\web_dist\index.html'`.
5. `builder/source/README.txt` — 故障排查 entry for the web dashboard. The DEPLOYED README is the RENDERED template (real versions, not `{{PLACEHOLDER}}`) — apply the same insertion to it, do NOT copy the template over it.

### Sync contract (deployed copies)

`runtime/bin/hermes-cli.cmd`, `scripts/*.ps1` in the running portable must stay BYTE-IDENTICAL with the builder sources. Diff first (`cmp`), then copy or apply the same targeted edit.

### EOL / BOM gotchas (all hit in practice)

- `.ps1` sources are UTF-8 **with BOM** (PowerShell 5.1 needs it for non-ASCII). The text patch tool can STRIP the BOM on some files — check after patching with `head -c 3 file | od -An -tx1` (expect `ef bb bf`) and restore if missing. (Verify-Portable.ps1 was originally BOM-less ASCII — match the file's own original state, don't blindly add BOMs.)
- `hermes-cli.cmd` must stay CRLF. `write_file` can emit LF-only — rewrite with explicit `\r\n` (python) and verify no lone LF: `python -c "d=open(p,'rb').read(); print(d.replace(b'\r\n',b'').count(b'\n'))"` → 0.
- The README template contains NUL bytes (read_file reports it as binary) — use byte-level insertion with python (`data.find(anchor)`), not the text patch tool.
- Validate edited ps1 without running: PowerShell parser — `[System.Management.Automation.Language.Parser]::ParseFile($f,[ref]$t,[ref]$e)`.
- After `npm install` in a workspace repo, confirm `git status --porcelain` is still empty (npm can rewrite the root lockfile); the build scripts restore it.

### Rejected alternative

Python-side patch (make `web_server.py` WEB_DIST resolution HERMES_DESKTOP-aware) is more thorough (covers direct `python -m hermes_cli.main dashboard` calls) but requires a new python-source patch apply/remove/reapply lifecycle — the patch stage only handles Electron files and would need to run after every `hermes update`. The `.cmd` change covers the realistic paths with zero upstream patch surface.

### Verified end-to-end

With the FIXED launcher, while `HERMES_WEB_DIST=<desktop bundle>` was still in the shell env and NO manual override: dashboard did NOT print "Using web dist from ...app.asar" → served `__HERMES_SESSION_TOKEN__` + the web bundle hash. Stamp file `<HERMES_HOME>/web-ui-build-stamp.json` created on first build → subsequent starts skip the build (stamp lives OUTSIDE the repo so `git clean` never touches it — but in-repo `web_dist` IS wiped by clean without the `-e` exclusion).

---

# Appendix: Portable install diagnosis (merged from portable-install-diagnosis.md)

## Portable-install diagnosis: "Desktop IPC bridge is unavailable."

First encounter: 2026-08-04, Hermes Desktop Portable at `D:\Hermes-Agent-Portable`. `hermes dashboard` started fine and served http://127.0.0.1:9119, but the page (opened in the desktop app's preview pane) showed the toast **"Desktop IPC bridge is unavailable."** and the chat was disabled.

### Root cause

- The desktop app sets `HERMES_WEB_DIST` to its own `app.asar.unpacked\dist` in its process environment (it spawns dashboard backends and embeds them in a webview that DOES have the bridge).
- Hermes terminal tool / bash sessions spawned from the desktop app inherit that variable; `hermes-cli.cmd` itself never sets it.
- `web_server.py`: `WEB_DIST = Path(os.environ["HERMES_WEB_DIST"]) if "HERMES_WEB_DIST" in os.environ else Path(__file__).parent / "web_dist"` — the inherited value wins over the default.
- Compounding: the install had no `hermes_cli/web_dist` at all — the web UI had simply never been built (`web/vite.config.ts`: `outDir: "../hermes_cli/web_dist"`, the server's DEFAULT fallback).
- The DESKTOP bundle hard-fails without the bridge: its boot code checks `window.hermesDesktop` and on absence shows the toast and disables chat; it contains NONE of the web-mode globals (`__HERMES_SESSION_TOKEN__`, `__HERMES_AUTH_REQUIRED__`, `__HERMES_BASE_PATH__`) — desktop-only, not dual-mode. `window.hermesDesktop` exists only inside the Electron app (preload script `electron-preload.js`).

### Verified fix (exact commands)

```bash
cd "D:/Hermes-Agent-Portable/data/hermes-home/hermes-agent/web"
npm install --no-audit --no-fund && npm run build
export HERMES_WEB_DIST="D:/Hermes-Agent-Portable/data/hermes-home/hermes-agent/hermes_cli/web_dist"
"D:/Hermes-Agent-Portable/runtime/bin/hermes-cli.cmd" dashboard --no-open
curl -s http://127.0.0.1:9119/ | grep -oE '__HERMES_SESSION_TOKEN__|index-[A-Za-z0-9_]+\.js' | sort -u
# expect: __HERMES_SESSION_TOKEN__ + the NEW bundle name (e.g. index-Cv1Lntuh.js)
```

### Code pointers (web_server.py, 源码)

- `WEB_DIST` resolution: `hermes_cli/web_server.py` (~line 135; line numbers shift — re-grep).
- SPA bootstrap injection (`__HERMES_SESSION_TOKEN__` etc.) and the `__HERMES_AUTH_REQUIRED__` switch: `_serve_index()` inside `mount_spa()`.
- Embedded chat always on: `_DASHBOARD_EMBEDDED_CHAT_ENABLED = True`; routes `/api/ws`, `/api/pty`, `/chat`.
- CORS restricted to localhost origins; auth gate list in `hermes_cli/dashboard_auth/public_paths.py`.

### Related facts

- Dashboard is a full backend (cron ticker, gateway pub/sub per-channel registry) — not a static server.
- `hermes dashboard --stop` kills it; `--status` reports running processes.
- The desktop app's own spawned dashboard backend SHOULD keep serving the desktop bundle (its webview injects the bridge) — don't "fix" that path.

---

# Appendix: uv-managed Python portable updates (merged from uv-managed-python-portable-updates.md)

## uv-managed Python updates in a Windows Portable bundle

Use this pattern when a Portable Electron/backend distribution bundles uv-managed CPython and must follow future patch releases without depending on a fixed directory name.

### Durable findings

- A selector such as `uv python install 3.11` resolves the current compatible patch release.
- The directory name is not authoritative. The same concrete Python may be represented by an exact-patch directory or a minor-series alias.
- Verify identity with both:
  - `uv python find 3.11 --managed-python`
  - `<resolved-python> --version`
- Windows PowerShell 5.1 can treat uv download progress on stderr as a terminating native-command error. Temporarily set `$ErrorActionPreference = 'Continue'`, capture output and `$LASTEXITCODE`, restore the preference, then validate with `uv python find`.

### Transaction design

1. **Normal launch/repair (offline):**
   - Read `runtime/python/current.txt`.
   - Resolve exactly `<python-root>/<pointer>/python.exe`.
   - Validate major/minor before use.
   - Do not query the network.

2. **Explicit updater mode:**
   - For Hermes Portable, parse the 更新后源码的 top-level `scripts/install.ps1` `$PythonVersion` before Python provisioning or update.
   - Pass that selector unchanged to `uv python install/find`, validate the resolved interpreter's major/minor against it, write the resolved exact runtime directory to `current.txt`, and rebuild/smoke-test the venv before pruning the previous runtime.
   - Keep the current pointer, Python, and venv intact until candidate validation succeeds.
   - Set `UV_PYTHON_INSTALL_DIR` to the Portable runtime root and disable registry/bin integration.
   - Run `uv python install <supported-minor>` without `--reinstall`.
   - Resolve with `uv python find <minor> --managed-python` and verify `python.exe --version`.
   - Build a replacement relocatable venv with the resolved interpreter.
   - Sync locked dependencies and smoke-test core imports.
   - Only after success: write the pointer atomically, delete the old venv backup, and prune other matching runtimes.
   - On any failure: delete the candidate venv, restore the old venv and pointer, retain the old runtime.

3. **Launcher:**
   - Read the pointer; do not wildcard-enumerate runtime directories.
   - If the pointer is absent or invalid, fail with a repair instruction; do not embed an originally bundled exact directory as a fallback because the official installer may change `PythonVersion` after a source update.
   - Print the missing full path on failure.

### Verification

- Initial archive contains one runtime directory plus `current.txt`.
- Launcher `--version` reports the expected Python.
- Move the bundle, run repair, and confirm `pyvenv.cfg` points beneath the moved root.
- Run updater mode against the current patch to prove idempotency.
- Simulate a failed candidate venv and verify old venv/pointer survive.
- Start the packaged Desktop and confirm backend readiness.
- Re-audit the final ZIP for one initial runtime, empty user state, and matching checksum.

### Pitfalls

- Do not choose which duplicate to keep solely from `pyvenv.cfg`; it may reflect accidental staging order.
- Do not treat an exact-looking directory name as proof that uv will always use exact naming.
- Do not use `--reinstall` during every update; it downloads the same patch needlessly.
- Do not delete old runtimes before candidate venv imports pass.
- Do not invoke updater-mode network work during ordinary app startup.

---

# Appendix: Windows managed-python dedup (merged from windows-managed-python-dedup.md)

## Windows managed Python deduplication

Use when a Portable ZIP contains multiple uv-managed CPython directories that appear to provide the same interpreter.

### Decision procedure

1. Inventory each immediate runtime directory: file count, bytes, `python.exe --version`, architecture, and DLLs.
2. Search launchers, repair scripts, `pyvenv.cfg`, generated shims, config, and build metadata for both directory names.
3. Inspect the upstream Windows installer for the selector passed to uv (for example `uv python install 3.11`).
4. Inspect real uv output or `uv python find <selector>` to determine the concrete patch release and canonical managed directory.
5. Treat the current venv path as evidence of present wiring, not authority over which duplicate is canonical.
6. Keep the canonical uv directory; delete the staging alias only after rebuilding the venv against the retained interpreter.
7. Replace wildcard launcher lookup with the exact retained path.

### Hermes example pattern

The official installer may request the minor selector `3.11` while uv downloads a concrete build such as:

```text
cpython-3.11.15-windows-x86_64-none
```

If a Portable staging history also produced:

```text
cpython-3.11-windows-x86_64-none
```

both can run Python 3.11.15. Prefer the exact canonical uv directory when installer/output evidence confirms it, rebuild the Portable venv against that directory, then remove the alias. (uv may also keep a minor-series JUNCTION pointing at the exact directory — one runtime, not a duplicate; retain the junction when `pyvenv.cfg` references it.)

### Required acceptance checks

- exactly one CPython directory remains;
- retained `python.exe --version` and architecture are correct;
- `pyvenv.cfg` points under the current Portable root to the retained directory;
- core imports pass from the venv; remember that a bare managed CPython may not import the application package because the wrapper supplies source and venv `site-packages` through `PYTHONPATH`;
- the direct wrapper succeeds with a harmless probe such as `--version` and reports the retained Python version;
- venv repair succeeds after moving the package root;
- Desktop backend starts and listens, and process inspection confirms the base interpreter executable comes from the retained canonical directory;
- launcher and repair scripts contain no wildcard interpreter selection and no deleted path;
- after editing a Windows `.cmd`, read it back before execution; if a targeted patch split a backslash sequence (for example `runtime` at `\r`), fully rewrite the short launcher with explicit CRLF;
- final ZIP inventory contains only the retained runtime directory and excludes uv scratch metadata (`.temp`, `.lock`, `.gitignore`) unless deliberately required;
- fresh extraction and startup pass;
- checksum is regenerated after deduplication.

---

# Appendix: Windows csc compilation (merged from windows-csc-compilation.md)

## Windows csc Compilation & Updater EXE Patterns

Use when compiling C# with the .NET Framework 4.0 command-line compiler
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) on Windows —
especially small launcher/updater executables compiled ad hoc (outside
MSBuild/Visual Studio), or when verifying that two compiled binaries are
logically identical. Canonical project: the Hermes Portable Builder compiles
`Hermes.exe` and `Update.exe` this way on every build and recompiles them
ad hoc when templates change.

### csc Invocation

```powershell
CSC="$WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
"$CSC" /nologo /target:exe /platform:anycpu /optimize+ `
  "/win32icon:<repo>\apps\desktop\assets\icon.ico" `
  "/out:<target>\Update.exe" /reference:System.Windows.Forms.dll `
  "<source>\Update.cs"
```

- `/target:exe` = console app (visible console window); `/target:winexe` = no
  console (GUI-only, used for the Hermes root launcher).
- `/win32icon:<path>` embeds the official app icon into the generated PE —
  required for root launchers; without it they inherit no icon. Verify the
  result with `[System.Drawing.Icon]::ExtractAssociatedIcon(<exe>)`.
- Keep `using System.Windows.Forms;` + `/reference:System.Windows.Forms.dll`
  for `MessageBox` dialogs.

### Pitfalls (all verified)

- **UTF-8 BOM is mandatory for non-ASCII source.** csc reads a BOM-less UTF-8
  `.cs` file as ANSI/GBK on Chinese-locale Windows and compiles wrong bytes
  (Chinese string literals silently corrupt, or CS1xxx mojibake errors).
  After editing a template with non-ASCII text, re-save before compiling:
  `[IO.File]::WriteAllText($p, $text, [Text.UTF8Encoding]::new($true))`
  (verify `EF BB BF` prefix). ASCII-only templates compile fine either way.
- **Stay .NET 4.0 / C# 4 compatible** with this compiler: no string
  interpolation `$"..."`, no inline `out var`, no
  `Contains(string, StringComparison)` (use `IndexOf(...) >= 0`), no `??`
  beyond C# 4 features. Write `delegate(object s, DataReceivedEventArgs e)`
  rather than lambdas for event handlers if unsure.
- **Two csc compiles of identical source are NOT byte-identical.** The PE
  timestamp (4 bytes at PE-header-offset+8; PE header offset is the uint32
  read from file offset 0x3C — typically 0x88) and the .NET module MVID
  (16-byte GUID in metadata) change on every build. To prove a recompiled exe
  matches a deployed one: diff the bytes and confirm every differing offset
  falls inside those two regions, then mask them and compare — equal after
  masking means identical logic.
- **Searching compiled strings**: method/type names live in metadata as
  UTF-8; user string literals live in the #US heap as UTF-16LE. To verify a
  Chinese message made it into the binary, search for
  `msg.encode('utf-16-le')`; to verify a method name, search plain ASCII
  bytes. A PowerShell `[Text.Encoding]::Unicode.GetString(bytes).Contains(...)`
  check for Chinese may fail on method names but succeed on literals — use
  the right encoding per symbol kind.

### Verifying a deployed/recompiled pair

1. Compile both exes from the SAME template with the same flags.
2. Byte-diff; allow only PE timestamp + MVID differences (see above).
3. Byte-verify the expected symbols with the correct encodings.
4. Check the PE icon with `ExtractAssociatedIcon`.
5. Clean up any throwaway test compiles.

### Reference

- The `Update.exe error dialog` appendix below — full session detail:
  redesigning `Update.exe` failure dialogs (four dialog shapes,
  child-output capture via async event handlers without deadlock,
  network-error classification, diagnostic log), shipping a template change
  to stage + deployed copies without a full rebuild, the github.com
  network-outage diagnosis ladder for failed updates, and the git "dubious
  ownership" fix after a Windows rebuild/SID change.

---

# Appendix: Windows console output encoding (merged from windows-console-output-encoding.md)

## Windows Console & Child-Process Output Encoding

Merged from the standalone `windows-console-output-encoding` skill on 2026-08-10.

### When to Use

Use when a Windows console app (C#/.NET launcher, PowerShell script, cmd
wrapper) streams child-process output and the text comes out wrong — `?`
marks, `□` tofu boxes, mojibake — or when capturing two redirected streams
deadlocks. Verified 2026-08-10 on zh-CN Windows against npm/node, git,
PowerShell 5.1, and bundled Python children.

### Root-cause ladder (in order)

1. **Decode mismatch (`?` or mojibake)**: .NET 4.0 `Process` decodes
   redirected child stdout/stderr with `Console.OutputEncoding` — the OEM
   code page, GBK/936 on zh-CN. Children that emit UTF-8 (npm/node, git,
   modern Python) get their multi-byte glyphs mangled. Symptom: one specific
   character becomes `?` (e.g. official npm postinstall `\u2705` checkmark).
2. **Glyph missing in console font (`□` tofu)**: after decoding is correct,
   conhost fonts (raster/Consolas/NSimSun) have NO emoji glyphs. U+2705
   (✅), U+2714 (✔), U+2713 (✓) render as boxes. This is a font limit, not
   an encoding bug — do not chase it as one.
3. **Capture deadlock**: reading stdout then stderr sequentially with
   `ReadToEnd()` blocks forever when the child fills the 4KB stderr pipe
   buffer while the parent waits on stdout EOF.
4. **Race on early pipeline close**: PowerShell 5.1 native output piped
   straight into `Select-Object -First 1` can kill the child with a broken
   pipe (exit -1) when it hasn't finished writing.

### Verified fix patterns

#### 1. UTF-8 decode + display (C# / .NET 4.0, e.g. compiled launchers)

At entry, BEFORE spawning any child; restore in `finally`:

```csharp
Encoding originalConsole = null;
try { originalConsole = Console.OutputEncoding; Console.OutputEncoding = Encoding.UTF8; } catch { }
try { Environment.SetEnvironmentVariable("PYTHONIOENCODING", "utf-8"); } catch { }
try { Environment.SetEnvironmentVariable("PYTHONUTF8", "1"); } catch { }
try { return MainBody(); }
finally { try { if (originalConsole != null) Console.OutputEncoding = originalConsole; } catch { } }
```

- `Console.OutputEncoding = UTF8` affects BOTH the Process decode side and
  the console display side in .NET 4.0 (there is no
  `ProcessStartInfo.StandardOutputEncoding` before .NET 4.5 — do not write
  code that requires it with the 4.0 csc).
- `PYTHONIOENCODING`/`PYTHONUTF8` force bundled Python to emit UTF-8 when
  its stdout is a pipe (it otherwise picks the console codepage).

#### 2. Same fix in PowerShell 5.1

```powershell
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8   # PS runs on .NET 4.5+, this property exists
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
```

#### 3. Emoji tofu — substitute only GBK-encodable glyphs

If a substituted glyph is required (display-only; keep the captured log in
original Unicode), the target MUST be encodable in GBK or console fonts
lack it too. Verified candidates on zh-CN: `v`, `[OK]`, `>`, `√` (U+221A).
NOT GBK-encodable: `✓` U+2713, `✔` U+2714, `»` U+00BB. User may prefer the
tofu box over any substitution — offer choices, don't assume.

#### 4. Double `WaitForExit()` in async capture

First `WaitForExit()` only guarantees the process handle exited;
`BeginOutputReadLine`/`BeginErrorReadLine` handlers may still be draining
on the thread pool, so a failure MessageBox can pop while output keeps
scrolling. Call a second parameterless `WaitForExit()` before reading the
captured buffer.

#### 5. Parallel stream capture (no deadlock)

```powershell
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()
$stdout = $stdoutTask.Result
$stderr = $stderrTask.Result
```

or the event-handler pattern with `BeginOutputReadLine`/`BeginErrorReadLine`
+ double `WaitForExit()` (see 4). Never sequential `ReadToEnd()` on both.

#### 6. `Select-Object -First 1` broken-pipe race (PS 5.1)

Collect the full output in a subexpression FIRST, then truncate:
`(& $exe ... ) | Select-Object -First 1` — this succeeds where
`& $exe ... | Select-Object -First 1` intermittently returns exit -1.

### Pitfalls

- A successful exit code does not prove the text was decoded correctly —
  mojibake is silent. Verify by searching the compiled binary for the exact
  string in the right encoding: user string literals live in the #US heap
  as UTF-16LE (`msg.encode('utf-16-le')`); method/type names in metadata as
  UTF-8.
- Don't set `Console.OutputEncoding` without a `finally` restore — a shared
  console (launched from cmd) stays on 65001 and breaks the caller's later
  GBK output.
- `cmd`'s `if` has NO wildcard support: `if "%~1"=="--port=*"` never
  matches. Use prefix slicing with delayed expansion:
  `set "ARG=%~1"` + `if /i "!ARG:~0,7!"=="--port="` + `set "PORT=!ARG:~7!"`.
- UTF-8 source files for csc MUST carry a BOM (`EF BB BF`) or csc reads
  them as ANSI/GBK and silently corrupts non-ASCII string literals.
- Don't record a "display sanitizer" as required — the tofu box is correct
  rendering of an emoji the font lacks; substitution is a UX choice.

### Verification

1. Reproduce the byte-level fault: `'✅'.encode('utf-8')` = `e29c85`; decode
   with GBK (`errors='replace'`) and observe the `?`/mojibake.
2. After the fix, echo the same string through the launcher and confirm
   correct rendering (or intentional tofu).
3. For compiled exes, verify the literal is embedded:
   `b'SanitizeForConsole' in data` style checks per encoding kind (above).
4. For capture fixes, run a child that writes >4KB to stderr while stdout
   stays open, and confirm the parent completes.

---

# Appendix: Windows PowerShell build execution (merged from windows-powershell-build-execution.md)

## Windows PowerShell Build Execution (from git-bash)

Merged from the standalone `windows-powershell-build-execution` skill on 2026-08-10.

### When to Use

- Launching a long PowerShell build/packaging script (`.ps1` under
  `$ErrorActionPreference='Stop'`) from the git-bash/MSYS terminal.
- Running such a script in the background and needing the REAL exit status
  from the completion notification, not the pipeline's last command.
- A build that "finished" but produced no artifact, or a background process
  reporting exit code 0 with no success line in the log.
- Diagnosing a PS 5.1 terminating error (`FullyQualifiedErrorId`) buried
  late in a long build log.
- Writing PowerShell one-liners from bash and hitting `$_` mangling or MSYS
  path conversion issues.

### Core pitfall: pipelines mask the script's exit code

`powershell.exe -File build.ps1 2>&1 | tee log | tail -100` makes the shell's
exit status that of `tail`, not PowerShell. A script that dies mid-run under
`$ErrorActionPreference='Stop'` still reports a pipeline "success": verified
2026-08-10 with `Hermes.ps1` — the background process reported
"completed normally (exit code 0)" while the log showed a terminating
`Get-Content : 找不到路径...` at line 581 and a dead build. The `$?`/exit
code of the LAST command in the pipeline is all the caller sees.

Correct patterns:

```bash
# Foreground: capture the real code from the FIRST pipeline stage
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$P" > build.log 2>&1
echo "BUILD_EXIT_CODE=${PIPESTATUS[0]}" | tee -a build.log
```

```bash
# Background with notify_on_complete: same shape, redirect to a log file,
# then echo PIPESTATUS[0] into the log so the completion notification and
# post-run grep carry the real code.
powershell.exe ... > run.log 2>&1
echo "BUILD_EXIT_CODE=${PIPESTATUS[0]}" | tee -a run.log
```

### Diagnosing a long build

- **Confirm a healthy start**: right after launch, read the first ~8 lines of
  the log (env sanitization, patch applied, first compile step) — catches
  immediate arg/quoting errors instead of waiting blind.
- **Never trust the reported exit code alone.** grep the log for the script's
  own failure markers: `failed with exit code N`, `FullyQualifiedErrorId`,
  the throw message, or absence of the final success line. PS 5.1 terminating
  errors print a full `+ CategoryInfo / FullyQualifiedErrorId` block; the
  message may be localized (e.g. Chinese `找不到路径` for PathNotFound).
- **A failure late in a long log is often the ONLY failure** — the hundreds
  of preceding lines are successful stages. Read around the error line, then
  check which prerequisite the failed step consumed (e.g. a version-stamp
  read from `node_modules` implies the install layout changed).
- Fix the root cause in the template/script, then re-run the FULL build
  (these scripts have no resume; the re-run reuses npm/electron caches so it
  is faster than the first).

### bash ↔ PowerShell interop

- **Every `$var` is mangled inside double quotes** — bash expands `$_` (last
  argument of the previous command), `$errs`, `$tokens`, `$null`, etc. before
  PowerShell ever sees them. A `-Command` like
  `"Where-Object { $_.ExecutablePath -like ... }"` silently corrupts, and a
  syntax check like `-Command "$tokens=$null; $errs=$null; [Parser]::ParseFile(...)"`
  fails with `EmptyPipeElement` because bash already emptied the variables
  (observed 2026-08-11, re-paid 2026-08-24; see also the HARD RULE in the
  main skill's Project Release Contract). The robust fix: write the
  one-liner to a temp `.ps1` file and run `powershell.exe -File <temp>`
  (delete it after), or single-quote the whole `-Command` argument, or avoid
  `$` variables entirely.
- **MSYS paths are not reliable for native tools** (7za, csc, etc.). Pass
  native `D:\...` paths or cwd-relative paths, not `/d/...`.
- **CRLF + targeted patch engines**: when editing CRLF `.cmd`/`.ps1`
  templates, re-read the file after patching; backslash sequences like `\r`
  can be mishandled. Prefer a full rewrite with explicit CRLF for short
  launchers.

### Dependency-layout assumptions break silently

Build scripts that read version stamps from `node_modules` assume npm's
hoisting. npm hoists a workspace dependency to the ROOT `node_modules` only
when the root manifest also participates; a dependency declared solely in a
workspace (e.g. `electron` in `apps/desktop/package.json` after upstream
removed it from root `package.json`) stays in
`<workspace>/node_modules/`. Verified 2026-08-10: `Hermes.ps1`
died reading `node_modules\electron\package.json`; the fallback
`apps\desktop\node_modules\electron\package.json` fixed it. When a
version-lookup fails, check BOTH root and workspace `node_modules`, and make
the lookup fall back across both instead of assuming hoisting. Verify an
installed package's actual location with `node -e
"console.log(require('./apps/desktop/node_modules/electron/package.json').version)"`
rather than trusting `ls node_modules`.

### PowerShell child-process capture pitfalls (verified 2026-08-10)

- **Proving a fix landed in a compiled .NET exe: count CALL SITES, not
  metadata strings.** A method name (e.g. `Process.WaitForExit`) appears in
  metadata ONCE no matter how many times it is called — the MemberRef table
  dedupes it — so `b'WaitForExit' in exe_bytes` or a metadata string scan
  cannot distinguish one call from two. Load the assembly with reflection and
  count callvirt tokens resolved by name inside the method's IL:

  ```powershell
  $asm = [System.Reflection.Assembly]::LoadFile($exePath)
  $m = $asm.GetType('Program').GetMethod('RunCaptured', [Reflection.BindingFlags]'NonPublic,Static')
  $il = $m.GetMethodBody().GetILAsByteArray()
  $count = 0
  for ($i = 0; $i -lt $il.Length - 5; $i++) {
    if ($il[$i] -eq 0x6F) {   # callvirt opcode
      $tok = [BitConverter]::ToUInt32($il, $i + 1)
      try { if ($asm.ManifestModule.ResolveMethod($tok).Name -eq 'WaitForExit') { $count++ } } catch {}
    }
  }
  ```

  Verified 2026-08-10: a "double `WaitForExit()`" fix showed 2 call sites via
  IL while a naive byte scan reported 1. Local variable names never appear in
  metadata at all — do not search for them. Also: two csc compiles of the
  same source differ ONLY in the PE timestamp (4 bytes at PE-header+8) and
  the 16-byte module MVID; diff and mask those to prove two exes are
  logically identical.
- **Sequential `ReadToEnd()` on two redirected streams can deadlock.** Reading
  `StandardOutput.ReadToEnd()` (blocks until EOF) BEFORE
  `StandardError.ReadToEnd()` hangs when the child fills the 4KB stderr pipe
  buffer while the parent still blocks on stdout EOF. Fix: start BOTH reads
  asynchronously, then wait, then take results (see the console-output
  appendix fix 5 for the code). PS 5.1 runs on .NET 4.5+, so
  `ReadToEndAsync()` exists.
- **`StartsWith($Root)` process-kill is a prefix-match landmine.** Killing
  "processes under the portable root" with
  `$_.ExecutablePath.StartsWith($Root, OrdinalIgnoreCase)` where `$Root` has
  no trailing separator ALSO kills a different install whose path merely
  begins with that root (`...-Portable-Beta\`, `...Portable 2\`). Found in
  BOTH `Repair-Portable.ps1` (then the `Repair` stage of `Update-Portable.ps1`) and the `SyncDesktop` stage of `Update-Portable.ps1`. Fix —
  match the directory boundary:

  ```powershell
  $rootPrefix = $Root.TrimEnd('\') + '\'
  $_.ExecutablePath.Equals($Root, [System.StringComparison]::OrdinalIgnoreCase) -or
  $_.ExecutablePath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)
  ```

  Grep the codebase for other bare `StartsWith($Root)` / `StartsWith(root)`
  uses after fixing one occurrence — the bug class repeats.
- **Child output encoding: UTF-8 bytes decoded as OEM code page → `?`.** A
  .NET 4.0 console launcher that streams a child process (npm/node/git/python)
  decodes redirected stdout/stderr with `Console.OutputEncoding` — the OEM
  code page (GBK/936 on zh-CN). UTF-8-only characters in child output (e.g.
  npm postinstall's `\u2705` checkmark) then render as `?`. Verified 2026-08-10
  in `Update.cs`: the fix is `Console.OutputEncoding = Encoding.UTF8`
  BEFORE any child spawn (this governs both the Process decode and the console
  display), plus `Environment.SetEnvironmentVariable("PYTHONIOENCODING","utf-8")`
  and `PYTHONUTF8=1` (bundled Python emits GBK when its stdout is a pipe
  otherwise), restoring the original encoding in a `finally`. Note the follow-on
  layer: after correct decoding, emoji still render as `□` because conhost
  fonts (raster/Consolas/NSimSun) have no emoji glyphs — and `✓`/`✔` are NOT
  GBK-encodable, so the only safe glyph substitutes are ASCII or `√`.
  MessageBox dialog strings (UTF-16LE literals) are NOT affected — the
  corruption is only in streamed child output.

### Windows cmd scripting pitfalls

- **`if` in cmd has NO wildcard support.** `if /i "%~1"=="--port=*"` never
  matches (unlike PowerShell's `-like`), so `--port=9120` silently fell
  through and the default port was used — only the space form worked.
  Fix with prefix slicing + delayed expansion (`EnableDelayedExpansion`
  required):

  ```cmd
  set "ARG=%~1"
  if /i "!ARG:~0,7!"=="--port=" ( set "PORT=!ARG:~7!" & shift & goto :parse )
  ```

  Verify with a throwaway `.cmd` harness that echoes the parsed value
  (`echo RESULT_PORT=!PORT!`), run via `cmd /d /c test.cmd --port=9120` —
  never trust the parsing by eye.

### Stage-tree state verification

A staged/assembly tree is not guaranteed pristine build output. A user
running the updater (`Update.exe`) against the staged tree mutates it: the
内嵌源码 advances to a NEWER official commit than the builder's
`upstream\`, an interrupted flow leaves the source patch applied (`git status`
shows `M` on the patched files) and repair backups (`venv.portable-repair-old/`)
behind, while root README/launcher timestamps still show the OLD build.
Before claiming a stage is the fresh build: check
`git -C <stage> log --oneline -1` against the builder's upstream HEAD and
`git status --porcelain` must be empty. Do not salvage an updated stage —
the build script wipes and recreates it.

### Official repo-owned updater script design (upstream desktop-update)

Upstream ships its OWN repo-owned desktop update hand-off script
(`scripts/desktop-update.ps1`, an 11-line COMPAT FORWARDER since 2026-08-18;
the real logic moved to `scripts/desktop-update/windows.ps1`, 1149 lines —
the 2026-08-10 entry recorded 429 lines at the old path) that lives IN the
源码 so every `hermes update` refreshes the code driving the next update
(the "frozen-binary problem": compiled updaters go stale). When reviewing or
redesigning a custom updater, its notable patterns are:

- **FAIL CLOSED double gate**: (1) wait up to 30s for the Desktop pid to
  exit, abort with a clear message if it does not; (2) probe the venv shim
  for an exclusive file-open lock for up to 20s, abort rather than `--force`
  past a locked venv — a forced half-updated venv is the classic brick.
- **Result-file hand-off**: every exit path (success/abort/crash) writes
  `.hermes-update-result.json`; the relaunched Desktop reads+deletes it on
  boot so the user SEES how a detached update ended.
- **One automatic retry** of `hermes update` when it fails (exit code not 0
  and not the "close all windows" code 2) — the boundary class where new
  code is on disk but old code runs in memory. NOTE: retry was REMOVED from
  the Portable Update.cs on 2026-08-15 at the user's request (retrying does
  not fix real failures such as the cryptography DLL lock; it only doubles
  the wait).
- **Detecting upstream's "fake success"**: official `hermes update` treats a
  Desktop GUI build failure as non-fatal (prints a warning, exits 0) — a
  desktop-driven updater must grep the output for `Desktop build failed` and
  retry the build, or it relaunches the OLD exe and calls it success.
- **Marker ownership**: claim `.hermes-update-in-progress` with own pid;
  cleanup deletes it ONLY while still owned (a partner that rewrote it keeps
  its claim). Use `File.WriteAllText` for byte-exact LF framing when other
  readers (Rust/TS/Python) parse the marker.
- **Detached relaunch**: spawn the GUI via WMI
  (`Invoke-CimMethod Win32_Process Create`) instead of as a console child —
  Electron calls `AttachConsole(ATTACH_PARENT_PROCESS)` at boot and would
  latch onto the updater's console, so the console outlives the script and
  closing it kills the fresh GUI. WMI parents the process to WmiPrvSE.exe.
  Then `AllowSetForegroundWindow` + poll `MainWindowHandle` to hand focus to
  the relaunched window.
- **UTF-8 end to end**: `[Console]::OutputEncoding = UTF8`, child
  `StandardOutput/ErrorEncoding = UTF8`, `PYTHONIOENCODING=utf-8`,
  `PYTHONUTF8=1` — the same chain a console launcher needs (see the encoding
  pitfall above).

A custom Portable updater that already covers some of this (external-install
protection via `HERMES_DESKTOP_CHILD_PID`, atomic app swap with rollback,
Python-selector following) can still borrow the FAIL CLOSED gate, result-file
hand-off, and fake-success detection.

### Verification

1. Real exit code: `BUILD_EXIT_CODE=0` in the log AND the script's terminal
   success line present (e.g. `Portable release built: <zip>`).
2. Output artifact exists with the expected timestamped name.
3. No terminating-error block (`FullyQualifiedErrorId`) anywhere in the log.
4. The 内嵌源码 is clean afterward (`git status --porcelain`
   empty, `git stash list` empty) when the script guarantees that invariant.
5. When a stage is involved, its 源码提交 matches the builder's
   upstream HEAD and its `git status --porcelain` is empty.

---

# Appendix: Windows PowerShell native commands (merged from windows-powershell-native-commands.md)

## Windows PowerShell 5.1 Native-Command Pitfalls

Use when writing or debugging PowerShell 5.1 scripts that invoke native
executables (uv, git, npm, csc, 7za, python...) on Windows — build scripts,
update/repair helpers, or any automation. These are the failure modes that
look like tool bugs but are actually PowerShell pipeline/stream semantics.
All pitfalls below are verified with reproduction data.

### Pitfall 1: `native.exe | Select-Object -First 1` intermittently exits -1

**Symptom:** a wrapped native call fails with `... failed with exit code -1`
and NO stderr output, intermittently (e.g. 2/3 runs), while the same command
succeeds when run manually from bash or a standalone PowerShell. Classic
signature: the step right before it (using `| Out-Host`) always succeeds, the
`Select-Object -First 1` step flakes.

**Root cause:** `Select-Object -First 1` closes the pipeline after emitting
the first object. If the native process is still writing stdout at that
moment, it receives a broken pipe and exits with code -1 (0xFFFFFFFF). It is
a timing race, so the failure rate depends on how fast the native tool
flushes (uv 0.12.0 `python find --managed-python`: 2/3 failures).

**Fix:** collect the full output in a subexpression first, then truncate:

```powershell
# BAD  — race
$out = & $exe python find 3.11 --managed-python | Select-Object -First 1
# GOOD — stable (verified 5/5)
$out = (& $exe python find 3.11 --managed-python) | Select-Object -First 1
```

`| Out-Host`, `| Out-Null`, `| Out-String` consume all output, so they are
safe — the race is specific to early-terminating selectors like
`Select-Object -First 1`.

**Audit:** grep every script for `& <native> ... | Select-Object -First 1`
(also inside scriptblocks passed to a checked-invocation helper) and wrap the
native call in `(...)`. Found in the wild: `uv python find --system` /
`--managed-python` candidates, `git remote get-url`, `bash --version` probes.

### Pitfall 2: stderr lines abort the script (NativeCommandError)

**Symptom:** under `$ErrorActionPreference = 'Stop'`, the script dies at an
innocuous `& npm.cmd run ...` / `& $Uv ...` line with
`FullyQualifiedErrorId : NativeCommandError`, and the "error" text is an
informational notice (npm 12+ `npm notice`, uv "Using CPython ..." /
"Resolved N packages", git CRLF warnings, 7-Zip progress). `$LASTEXITCODE`
would have been 0.

**Root cause:** PS 5.1 treats ANY native stderr output as a terminating
error under EAP=Stop, even on exit 0. Any modern tool that writes notices to
stderr aborts the script at the first such line.

**Fix:** wrap every native invocation in a checked helper that relaxes EAP
and judges success purely by `$LASTEXITCODE`:

```powershell
function Invoke-NativeChecked {
    param([string]$What, [scriptblock]$Script, [switch]$AllowFailure)
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $Script; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $oldEap }
    if ($code -ne 0 -and -not $AllowFailure) { throw "$What failed with exit code $code" }
    if ($code -ne 0) { return }
    $output
}
```

Do NOT use `2>&1` inside the captured scriptblock — it merges stderr
ErrorRecords into the captured output and pollutes return values. A
`-AllowFailure` switch returns `$null` on nonzero exit for commands whose
nonzero exit is meaningful (`git diff --quiet`, `git remote get-url` with no
remote); callers then read `$LASTEXITCODE` themselves.

### Pitfall 3: env-var leaks from a running app poison native resolution

A running desktop app can leak `UV_PYTHON_INSTALL_DIR`-style variables into
every agent/user terminal it spawns. A build script that does NOT sanitize
inherited env may resolve the wrong runtime (the live app's python, whose
DLLs are locked). Sanitize at script start, then re-set to the script's own
paths right before each native call:

```powershell
foreach ($v in 'UV_PYTHON_INSTALL_DIR','UV_PYTHON_INSTALL_BIN','UV_PYTHON_INSTALL_REGISTRY') {
    if (Test-Path "Env:$v") { Write-Host "Sanitizing leaked $v=$([Environment]::GetEnvironmentVariable($v))"; Remove-Item "Env:$v" }
}
```

### Pitfall 4: native Windows tools reject MSYS/git-bash paths

Invoking native Windows executables (7za, robocopy, Start-Process) from
git-bash with an MSYS-style path silently misbehaves — the tool does NOT
understand `/tmp/...`, `/c/...`, or `~/...`:

- `7za a /d/Hermes-.../dist/x.zip ...` reports "Everything is Ok" but the
  archive lands somewhere unexpected (e.g. an empty `D:\d\` dir) and
  `l`/`t` then fail with "cannot find the specified file".
- `robocopy /tmp/git55 "C:\Program Files\Git" ...` fails with "system
  cannot find the path specified" (error 3) because the source `/tmp/...`
  does not exist in Windows' view of the filesystem.

**Fix:** pass native `C:\...` paths (or `cygpath -w <msys-path>`), or run
the tool through PowerShell where `Join-Path $env:TEMP ...` yields native
paths automatically. When a curl/robocopy/7za call from git-bash "fails
mysteriously", check whether any argument is an MSYS path before debugging
the tool itself.

### Pitfall 5: archive-inventory grep from git-bash — backslash path regexes are unreliable

When inventory-checking `7za l` output (or any listing containing Windows
paths) from the Hermes terminal / git-bash, a regex that spells out the
backslashes in a path is NOT reliable. The Hermes terminal JSON layer + bash
layers collapse `\\` before grep sees it: `'runtime\\bin\\hermes-cli.cmd'`
arrives as `runtime\bin\hermes-cli.cmd`, where ERE/PCRE interpret `\b`/`\h`
as word-boundary/escape sequences instead of literal backslashes. Verified
2026-08-11: `grep -E 'runtime\\bin\\...'` matched only `hermes-tui.cmd` while
missing `hermes-cli.cmd`/`hermes-dashboard.cmd`; a re-run of the same pattern
against the same saved listing returned 0 — intermittent, not deterministic.

**Reliable alternatives (all verified):**

- `grep -F 'runtime\bin\hermes-cli.cmd'` — fixed-string match, no regex layer;
- match the basename token only, without the directory: `grep -iE 'hermes-(cli|tui|dashboard)\.cmd'`;
- let `.` match the separator: `grep -Ec 'runtime.bin'` (5 hits, includes dir entry).

`7za l` output itself uses single backslashes and ends lines with `$` (no CR
in the redirected file — use `cat -A` to confirm before debugging).

### Pitfall 6: flaky HTTPS downloads — retry with size check, not curl one-shots

On networks with intermittent TLS resets (github.com, 7-zip.org), a single
curl invocation is unreliable: it can return HTTP=000 / exit 28 (timeout) or
exit 35 (SSL connect error) on one attempt and succeed on the next, and a
`-r 0-1023` range probe that returns 302 only proves the redirect endpoint
is reachable — not that the real download completes. `curl -s ... && [ -s
file ]` can even pass while the file was never written.

**Fix:** loop with `Invoke-WebRequest -UseBasicParsing -TimeoutSec 300`
(not curl) and validate the saved file size:

```powershell
for ($i = 1; $i -le 10 -and -not $ok; $i++) {
    try {
        Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -TimeoutSec 300
        $ok = (Get-Item $out).Length -gt $minBytes
        if (-not $ok) { Remove-Item $out -Force -ErrorAction SilentlyContinue }
    } catch { Start-Sleep -Seconds 8 }
}
```

PowerShell's downloader proved more resilient than git-bash curl on the
same flaky endpoint in practice (2026-08-07: curl loop 0/8, IWR loop 1/10
with 58.9 MB saved). Back off between attempts (5-8s) and keep the size
floor — a "successful" download smaller than the known archive size is a
truncated one.

In `Hermes.ps1` (2026-08-08) the retry contract is unified:
`Invoke-DownloadWithRetry -Uri <url> -OutFile <file> [-Label <name>]`
retries 3x with a 5s pause (same shape as the git clone / PortableGit /
7-Zip sites), and `-ReturnContent` returns the response body (used for the
Node.js version-index page). ALL builder download sites now use it: git
clone (own loop), PortableGit, 7-Zip extra+7zr, uv zip, Node.js index + zip.
(uv's separate `.sha256` fetch/verify path was removed 2026-08-09 — the
archive's own CRC fails corrupt downloads at extraction, HTTPS + 3x retry
covers transient failures.) Do not reintroduce bare downloads; keep the size
floor on new download sites where a known archive size exists.

### Debugging recipe for "works manually, fails in script"

1. Reproduce inside a minimal script that mirrors the failing context
   (same env vars, same working dir, same wrapper function, empty target
   dir) — manual runs succeeding prove nothing about the scripted path.
2. Isolate the variable: run the native command in several pipe forms in a
   loop (plain, subexpression-wrapped, merged-stream), recording
   `$LASTEXITCODE` each time. A pattern that flakes in one form and is
   stable in another identifies the pipeline as the culprit.
3. Confirm the fix with repeated runs (5/5), then audit ALL occurrences of
   the pattern across every script in the same change — build templates and
   deployed helpers ship byte-identical and hit the same bug class.
4. Keep the build path single: a switch that skips part of a build
   (`-SkipDesktopBuild`-style) makes different code paths execute and can
   mask pipeline races — one path to reproduce, one path to fix.

### Related

- GitHub / git connectivity diagnosis (network-outage ladder, retry
  strategy, hosts-pinning): see the "Network-resilient upstream sync"
  pitfall in the main skill's Pitfalls section.

---

# Appendix: Update.exe error dialog (merged from update-exe-error-dialog.md)

## Update.exe Error-Dialog Redesign & Update-Failure Diagnosis (2026-08-06)

Session context: the Hermes Portable (`D:\Hermes-Agent-Portable`) failed
`hermes update` twice with a bare `System.Exception: Official Hermes update
failed with exit code 1.` dialog. Root cause was a transient github.com network
outage, but the dialog gave the user no reason. This reference records the
dialog redesign, the byte-verification recipe, the propagation workflow, and
the network-diagnosis ladder.

### 1. Failure dialog redesign (builder\source\Update.cs)

Four dialog shapes, all title "Hermes Update" (all implemented in Update.cs):

1. **hermes update failed** (Error icon): `更新失败：无法完成官方 Hermes 更新（退出码 N）。\n\n` + `ClassifyUpdateError(output)` + `\n\n详细日志：<diagLog>`
2. **Step failure** (Error icon) via `Fail(step, rc, message, output, diagLog)`: `更新失败：<step>（退出码 N）。\n\n<message>\n\n详细日志：<diagLog>` (steps: 进程占用 / 防重入 / 便携环境修复 / 移除便携补丁 / 更新 Python 运行时 / 桌面同步; note the fail-closed gates pass `rc=0` because no child command ran — the step name carries the meaning)
3. **Update OK but auto-launch failed** (Warning icon): `更新已完成，但无法自动启动 Hermes：\n<ex>\n\n请手动启动 Hermes.exe。`
4. **Uncaught exception** (Error icon): `更新失败：发生未预期的错误。\n\n<ex>\n\n详细日志：<diagLog>`

`diagLog` = `data\hermes-home\logs\Update.exe-diagnostic.log`, written at start with a timestamp header; on failure `AppendDiag` appends step, exit code, and the child's full output. Every dialog prints the log path so the user can escalate.

#### Error classification (ClassifyUpdateError) — order matters

- **Network** (checked first): `network error`, `failed to connect`, `could not connect`, `recv failure`, `connection was reset`, `unable to access`, `timed out`, `timeout` → "无法连接到 GitHub（网络错误）… 检查网络或代理后重新运行 Update.exe。更新流程是安全的，重复重试不会损坏现有安装。"
- **DNS**: `could not resolve host`, `name resolution`, `dns`
- **Blocked**: `authentication`, `permission denied`, `access denied`, ` 403`, ` 401` (note leading space to avoid substring false hits)
- **Fallback**: first 6 non-empty output lines, labelled 输出摘要

### 2. RunCaptured: capture + live stream without deadlock

Replace `Run()` (which returned only the exit code) with a capture variant (async event handlers + `WaitForExit`, `lock(sb)` because the two event threads append concurrently; never `ReadToEnd()` before `WaitForExit()` — that deadlocks when the buffer fills). The current `RunCaptured` additionally has a double `WaitForExit()` and a `silent` overload (see the main skill's Pitfalls — Update.cs/console-launcher entries).

### 3. Byte-verification recipe (prove deployed == recompiled)

csc output is non-deterministic: the PE timestamp and the .NET module MVID (16-byte GUID) change per compile. Verified on 2026-08-06:

```python
import struct
a = open(deployed, 'rb').read(); b = open(recompiled, 'rb').read()
pe_off = struct.unpack('<I', a[0x3C:0x40])[0]      # PE header offset (0x80)
ts = range(pe_off + 8, pe_off + 12)                # PE timestamp (4 bytes)
diffs = [i for i in range(min(len(a), len(b))) if a[i] != b[i]]
non_ts = [d for d in diffs if d not in ts and d not in mvid_range]
# mvid_range = the 16-byte GUID block found by diffing (~0x25xx here)
assert not non_ts                                  # only timestamp+MVID differ
```

Observed: 18 differing bytes total — 2 at 136–137 (PE header area) and 16 at 9604–9619 (MVID). After masking those, files are byte-identical. NOTE: the MVID offset is per-build; locate it by diffing rather than hard-coding.

#### String-encoding gotcha when verifying compiled messages

- Method/type names → metadata, stored UTF-8. `b'ClassifyUpdateError' in data` works.
- String literals → #US heap, stored UTF-16LE. Chinese dialog text must be searched as `'无法连接到 GitHub'.encode('utf-16-le')`.
- A single `[Text.Encoding]::Unicode.GetString(bytes)` scan finds literals but misses method names; PowerShell console mojibake can also hide hits — use Python for byte-level checks.

### 4. Shipping a template change without a full rebuild

Propagate to ALL copies in one pass (verified 2026-08-06):

1. Edit `builder\source\Update.cs`; re-save UTF-8 **with BOM** (`[IO.File]::WriteAllText($p, $text, [Text.UTF8Encoding]::new($true))`).
2. Compile stage: `/out:D:\Hermes-Agent-Portable-Builder\stage\...\Update.exe`
3. Compile deployed: `/out:D:\Hermes-Agent-Portable\Update.exe` (same csc line incl. `/win32icon:<repo>\apps\desktop\assets\icon.ico`).
4. Re-render README.txt everywhere from `builder\source\README.txt` with the same `{{VAR}}` substitution the build uses — extract the current variable values (version/commit/electron/python/node/git/uv) from an existing rendered README via regex, then substitute into the template and write UTF-8 no-BOM, preserving line endings.
5. Old dist ZIPs keep the old behavior until the next full build (expected).

### 5. github.com network-outage diagnosis ladder

Symptom: `hermes update` dies at "Fetching updates..." with `Failed to connect to github.com:443 after ~21000 ms`.

1. `git remote -v` → confirm the official URL. **Do not trust the rendered error text**: Hermes wraps bare URLs as `@url:https://…` display-layer link syntax; the real URL in `logs\update.log` was the standard one. Not a config bug.
2. Preconditions for a safe re-run: `git status --porcelain` empty, `git stash list` empty, no patch markers in desktop sources, venv import OK. Remove stale `data\hermes-home\state-snapshots\*-pre-update` dirs left by failed runs.
3. Probe: `git ls-remote --heads origin main` ×5; `curl -sI https://github.com` (21s timeout / HTTP 000 = unreachable); controls: `curl -sI https://api.github.com` + a local site (baidu.com). api.github.com and local sites reachable while github.com:443 times out ⇒ transient github.com-main-domain outage. Retry later or via proxy; no config change. (For DNS-edge pinning, see the main skill's "Network-resilient upstream sync" pitfall.)
4. Update.exe is idempotent; repeated failed runs leave only stale `state-snapshots` dirs to clean.

### 6. git "dubious ownership" after a Windows rebuild/SID change

`fatal: detected dubious ownership in repository at '...' owned by:
(inconvertible) (S-1-5-21-...) but the current user is: PC-.../Administrator
(S-1-5-21-...)` — git refuses every pre-existing repo until each is whitelisted:

```bash
git config --global --add safe.directory D:/path/to/repo   # per repo, once
```

Add upstream, stage 源码, and 已部署源码 paths. Harmless to apply
proactively after a machine rebuild; the fix message git prints is the exact
command to run.

## 7. Update.exe behavior history (2026-08-10..26, consolidated from SKILL.md 2026-09-02)

### 2026-08-15 review — six findings, all fixed in builder source + recompiled copies
1. fail-closed gate aborts (`Fail("进程占用")`/`Fail("防重入")`) now return rc=1 (were 0 — `Finish(0)` wrote `"ok":true` and silently lost failures).
2. `StopPortableProcesses` uses directory-boundary `StartsWith('<root>\')` (prefix-collision bug class).
3. standalone-uv fallback (`uv pip install --python <venv> -e .` after a failed `hermes update`) only clears rc when the pre-update HEAD advanced (`RunGitHead` rev-parse HEAD) — no more fake "Update complete." with un-updated source.
4. `RunCaptured(cli, "update", …)` instead of the accidental `-m hermes_cli.main update` (`-m` is the `--model` alias; parse_known_args made it work by accident).
5. `FindForeignDashboardPids` uses a PowerShell `Get-CimInstance Win32_Process` pipeline with `-match 'serve|dashboard'` + `-notlike '<root>\*'` emitting bare pids (wmic.exe removed on Win11 24H2+).
6. `RunGitHead` runs silent (4-arg forwarding overload + 5-arg `silent` overload; silent still captures into the diag log, just skips Console forwarding).

### GUI updater (2026-08-26, adopted from the DeepSeek builder)
WinForms window: version labels + 检查更新/立即更新 buttons + live streaming log; `--check` (tray/CLI entry) opens the window and runs one check on load. Check = official `hermes update --check` (fetch without install; parseable "✓ Already up to date." / "⚕ Update available: N commits"). Update = full chain on a BackgroundWorker, streamed via `RunStreaming` (async Output/ErrorDataReceived + double WaitForExit + ManualResetEvent drain — never ReadToEnd). `--check` never claims the re-entrancy marker (read-only, so a tray check works while an update runs).
Porting pitfalls: (a) csc 4.0 is C# 5 — no `out var`, declare variables first; (b) inside a `Form` subclass, `Size`/`Point`/`Font`/`ContentAlignment`/`Color` collide with inherited `Control` members — fully qualify as `System.Drawing.*` (CS0118); (c) add `using System.Drawing;` + `System.ComponentModel;` + `System.Threading;`; (d) compiling csc from git-bash: forward-slash paths AND `-`-prefixed options (`-out:`), otherwise bash/MSYS mangles `/out:` (CS1504); (e) must compile `/target:winexe` — `/target:exe` makes a CONSOLE-subsystem app that pops a black cmd window next to the form; set `CreateNoWindow=true` on EVERY child ProcessStartInfo. Test before porting with a window + streaming-child spike (45s timeout), run inside the stage portable with `--check`.
Success path (2026-08-26, mirrors the DeepSeek Harness updater): `RunWorkerCompleted` (UI thread) pops MessageBox「更新完成：<version>\n\n是否立即重启 Hermes？」Yes/No — Yes → `Process.Start(Hermes.exe)` + `Close()`; No → window stays open (`SetBusy(false)` runs before the branch, buttons disable after; closing via X no longer asks the busy confirm). `RunFullUpdate` success tail no longer `Process.Start` — only `return Finish(0, "Update complete.")`. Version from `ReadCurrentVersion(root)` (pyproject.toml).
Repeated-check log stacking (2026-08-26, mirrors the DeepSeek updater): `CheckForUpdates` appends the full streamed `hermes update --check` output to the log box each click → double-clicking stacks two vertical copies. FIXED: `_txtLog.Clear()` at the start of each new check/update (after `SetStatus("正在检查更新...")` in CheckForUpdates, after the busy guard in RunUpdate; both on the UI thread — no race, buttons are disabled while busy and the double WaitForExit + UI-thread completion callback drain the previous round before the next click).

### Official upstream scripts/desktop-update.ps1 (since 2026-08-18)
An 11-line COMPAT FORWARDER to `scripts/desktop-update/windows.ps1` (1149 lines) — the repo-owned update hand-off the Desktop Update button spawns. (Earlier entry said 429 lines at the old path; the logic moved.) Solves the FROZEN-BINARY problem (staged exe has no self-update path, so updater fixes only ship with new installers; a repo-owned script refreshes every `hermes update`). Ideas adopted into Update.cs (2026-08-10): (a) FAIL-CLOSED preflight gates — wait Desktop process exit (30s) + venv shim unlock (20s), abort with nothing changed; Portable equivalent `WaitForRootProcessesExit(root, 30)` scans Hermes.exe/python.exe under the root (dir-boundary); (b) ONE retry of `hermes update` on exit != 0 && != 2 — REMOVED 2026-08-15 at the user's request (retrying does not fix real failures such as the cryptography DLL lock; it only doubles the wait); (c) result-file hand-off: every exit path writes `data\hermes-home\.hermes-update-result.json` `{ok, exit_code, message, finished_at}` via a unified `Finish()`; `Hermes.cs ShowUpdateResultIfFailed` reads+deletes it on next launch and pops a dialog when ok=false, so a detached/crashed update is never silent; (d) re-entrancy marker `data\hermes-home\.hermes-update-in-progress` containing OUR pid, reclaimed when stale (pid dead), removed only while owned. The official script lands in the 打包源码 automatically (part of upstream) and ships inert in every Portable (the Desktop Update button is not wired to Portable's Update.exe flow).

### Byte-verification of compiled strings (2026-08-26)
Whole-exe `[Text.Encoding]::Unicode.GetString($bytes).Contains('中文')` is unreliable — the Chinese needle can be corrupted through the PowerShell command-line layer, and whole-file decode misreads PE header/resources; observed false negative on the same file ("更新失败" hit while "是否立即重启" missed). Reliable method: convert the target string's UTF-16LE bytes to hex (是=2F 66) and search byte-level. String missing = definitely an old build; string present ≠ behavior correct (use it to prove staleness, not correctness).

# Appendix: Packaged .git conversion (consolidated 2026-09-02 from SKILL.md)

## Shallow-ization of the packaged checkout
`Convert-PackagedGitToShallow` (Hermes.ps1): full 737MB history → ~69MB depth-1 shallow. Sequence: `git fetch --depth 1 --no-tags <local-upstream> main` → `git reset --hard FETCH_HEAD` → `git remote remove origin` + re-add by URL → delete ALL local tags → `git reflog expire --expire=now --all` → `git gc --prune=now --aggressive`. Official `hermes update` handles shallow checkouts (verified 2026-08-23 end-to-end on a moved root; it preserves the boundary by passing `--depth 1` on its fetch — update_cmd.py `depth_args`; do not rely on exact line numbers, they shift).
PITFALL 1: `git clone --depth 1 <local-path>` is silently IGNORED by git's `--local` hardlink optimization (full history, is-shallow=false) — must use fetch --depth 1.
PITFALL 2: ~1600 origin remote-tracking branch refs plus version/backup tags keep history objects alive; gc cannot prune them — drop the origin remote (re-add by URL) and delete ALL tags FIRST, then gc.
PITFALL 3: `git fetch --depth 1 <local-path> main` from MSYS bash needs Windows forward-slash paths (`C:/...`); in PowerShell source use `$Repo.Replace([char]0x5C,[char]0x2F)` (patch-tool double-backslash trap).
PITFALL 4 (2026-08-23, three failed builds): Hermes.ps1 MUST keep its UTF-8 BOM (EF BB BF) — PS 5.1 decodes no-BOM files as ANSI/GBK; a Chinese comment's dangling byte swallows the following newline and the next code line is absorbed into a `#` comment and silently never runs. Text edit tools strip the BOM; re-add it after any such edit and verify with a `Get-Content -Raw` line count.

## Loose refs/heads/main residue (2026-08-27)
gc --prune=now --aggressive packs `refs/heads/main` into packed-refs and leaves `refs/heads/` EMPTY inside the ZIP. Overlay extraction (7za -y) over an older deployment then KEEPS the old loose `refs/heads/main` file, and loose-refs-shadow-packed-refs makes the packaged checkout silently resolve to the PREVIOUS release's commit — `git log` shows yesterday's commit, `git status` shows hundreds of modified tracked files, `hermes update` autostashes. Does NOT affect launch (app runs fine). FIXED in Hermes.ps1 (Convert-PackagedGitToShallow, after the gc): `git update-ref refs/heads/main $commit` writes a loose ref pinned to the packaged commit so every future overlay overwrites the stale file. One-time remedy for an ALREADY-deployed copy (no deletion): `git -C <root>\data\hermes-home\hermes-agent update-ref refs/heads/main <shipped-sha>`.

## Case-collision paths (2026-08-17)
Upstream can track two paths that differ only by case (observed: `contributors/emails/agent@Agents-Mac-mini.local` vs the lower-case variant, BOTH in origin/main with DIFFERENT contents). On Windows' case-insensitive filesystem only one materializes, so `git status --porcelain` ALWAYS reports the other as modified and release gates throw. FIXED in Hermes.ps1 `Protect-CaseCollisionEntries`: group tracked files by `ToLowerInvariant()`, mark `update-index --skip-worktree` on every entry whose `HEAD:<path>` blob differs from the working-tree hash-object. TWO call sites required (the staged status gate and the packaged status gate): the flag lives per-entry inside `.git\index`; the packaged-checkout block replaces the whole `.git` (fresh index, no flags) then rebuilds the index with `git rm -r --cached` + `reset --hard`, dropping any surviving flag — so the packaged gate must re-run the protection after the index rewrite. Bonus: the flag ships inside the packed `.git` index, so user-side `hermes update` local-changes checks stay clean. Do NOT "fix" this in the upstream checkout — it must stay a byte-exact mirror.

## CRLF stat-cache / stale-index trap
robocopy preserves source mtimes → the staged `.git`'s stat cache makes `git reset --hard` SKIP rewriting files: a CRLF working tree (copied from an `autocrlf=true` upstream) stays CRLF while the index expects LF. The build's own `git status` check passes via the stat cache, but on the END-USER machine ZIP extraction changes mtimes → git re-hashes → thousands of "M" entries → `hermes update` reports "Local changes detected" → stash → "Restore local changes now? [Y/n]" even on a perfectly clean install. Compounding: copying upstream's `.git` brings its build-dirtied index (patches were `git add`-ed during the build), so `checkout-index -f -a` still ships dirty files. Fix (in `Hermes.ps1`, after replacing the packed `.git` and setting `core.autocrlf=false`/`core.eol=lf`): `git rm -r --cached --quiet .` then `git reset --hard --quiet` — rebuilds index + working tree FROM HEAD, bypassing the stat cache — then THROW unless `git status --porcelain` is empty. Verified: extracted package is CLEAN (0 modified), update completes with no stash prompt. (EOL-only false-positive variant and its cleanup are in the `Desktop patch line endings` appendix.)

## tmp_pack_* bloat (2026-08-23)
Interrupted git operations can leave `tmp_pack_*` files in the packaged 源码's `.git\objects\pack\` that inflate the archive by hundreds of MB (one build shipped a 469 MB tmp_pack). Delete immediately before archiving — the objects they carry are already in the object store. A desktop app whose backend fails (broken venv) triggers its auto-repair (`hermes update` → `git fetch`); on a blocked network the fetch hangs holding `FETCH_HEAD`/`tmp_pack` open and can lock the next build's stage removal. Kill ALL stage processes (Electron, python, git children — not just `Hermes.exe`) before building.

## Stage deletion timing (changed 2026-08-13, user request)
`Hermes.ps1` deletes the staged Portable directory at script start (`Remove-TreeSafe $Stage` + throw, right before the main flow's `Read-OfficialPythonSelector`) — targeting `stage\Hermes-Agent-Portable` ONLY, not the `stage\` parent (a plain container expected to stay empty). This is the ONLY stage deletion. Accepted trade-off (user-chosen): a failed build no longer preserves the previous staged tree, but an occupied-stage error now surfaces at script start instead of after the builds finish.

# Appendix: hermes update failure classes (consolidated 2026-09-02 from SKILL.md)

## DLL-lock class (2026-08)
Windows refuses to replace DLLs loaded by running processes. Two distinct subtypes:
- EXTERNAL lock: `cryptography/_rust.pyd` os error 5 — the desktop backend serve process holds it. `StopPortableProcesses(root)` (directory-boundary filter stopping Hermes/python, does not kill other pythons) runs before the official `hermes update` AND before Repair `-UpdatePython` (venv Rename-Item had been Access denied).
- SELF-LOCK (2026-08-26, stage-verified): `jiter\jiter.cp311-win_amd64.pyd` os error 5 — the locker IS the hermes update process itself (runtime python + `HERMES_PORTABLE_SITE_PACKAGES` sitecustomize mounts venv site-packages; `import hermes_cli.main` → openai → jiter). StopPortableProcesses cannot kill the self. FIXED in Update.cs: before hermes update, robocopy `/MIR` a site-packages copy to `site-packages.update-copy`, point `HERMES_PORTABLE_SITE_PACKAGES` at the copy (the updater loads from the copy, uv mutates only the real venv — no self-lock; copy failure falls back to a plain update), restore the env after, and delete the copy best-effort (a cold-start gateway may hold it → cleaned on the next StopPortableProcesses round). `hermes-cli.cmd`'s `HERMES_PORTABLE_SITE_PACKAGES` uses `if not defined` to respect an external preset (default unchanged). Sync source + stage copies.
- Symptom fingerprint: `hermes update`'s `uv pip install -e .` fails + venv leaves TWO cryptography dist-info dirs.
- Retry policy (user decision 2026-08-15): hermes update is NOT retried — retrying does not fix real failures (e.g. the DLL lock), it only doubles the wait.

## Detached-checkout class (2026-08-26, deployed install)
A previous failed update left the checkout with a HALF-APPLIED merge tree (old HEAD + partial new content as uncommitted changes; `git diff origin/main` showed ~3000 deletions). The official `hermes update` then stashes, pulls into a no-op and fails the whole chain (the portable uv fallback also refuses: HEAD did not advance). FIXED in Update.cs pre-update checkout repair: `git reset --hard HEAD` (discards tree/index changes; the branch ref stays put so `headBefore`/`RunGitHead` stays valid for the uv-fallback proof) then `git checkout main` (fallback `-B main`). The packaged source is a build artifact, so discarding is safe (the desktop patch was already removed by the earlier PatchRemove step). Do repairs through the updater, not by hand on a running app.

## Shallow-boundary corruption class (2026-08-26, same deploy as the detached-checkout class)
`hermes update` exits 1 with "(no output)" even though the fetch already landed the new commits on disk; `git log` fails with `Could not read <sha> ... Failed to traverse parents of commit <sha>`. ROOT CAUSE: the embedded shallow clone's `.git/shallow` lost HEAD's boundary marker — HEAD not listed as a boundary while its parent object is missing from the store — so EVERY history walk (log/rev-list/merge-base/merge --ff-only) dies and the official updater's git steps fail before printing anything. FIXED in Update.cs (same pre-update block): after the checkout repair, probe `git log -1 --oneline HEAD` (exit 128 on broken, 0 on healthy — whether shallow or full); on failure run `git fetch --depth 1 origin main` (best-effort boundary re-establishment) then `git reset --hard origin/main` — advances the source directly, trees-only, NO history walk, repo stays shallow. `headBefore` is captured BEFORE this repair, so the uv-fallback HEAD-advance proof still passes truthfully (the source DID advance). Manual repair for a deployed install (verified 2026-08-26): stop all processes under the root first, then `git -C <root>\data\hermes-home\hermes-agent reset --hard origin/main`, verify `git log -1` exits 0 and `git status --porcelain` is empty; the next `hermes update` sees commit_count==0 and early-outs on "Already up to date" (rc 0).

## merge --ff-only "unrelated histories" is NORMAL on shallow checkouts (2026-08-26, verified in a disposable depth-15 clone)
The official updater's own `git fetch --depth 1 origin main` RE-GRAFTS the shallow boundary at the fetched tip — even when nothing new arrives — so on a shallow embedded checkout `git merge --ff-only origin/<branch>` ALWAYS fails with `fatal: refusing to merge unrelated histories` (HEAD sits below the new boundary). This is NORMAL: the update completes via update_cmd.py's reset fallback ("⚠ Fast-forward not possible (history diverged), resetting to match remote..." + `git reset --hard origin/<branch>`, which moves HEAD without walking history), the "Code did not move" guard passes because pre/post pull SHAs differ, and `.git/shallow` boundary entries accumulate by design. Do NOT treat that warning (or the merge failure) as an error; only the "Code did not move — HEAD is pinned" guard is a real failure.

## Foreign-backend kill protection (2026-08-10..)
The official `hermes update` ends by STOPPING every running dashboard/serve process found by command-line match (stale-backend cleanup). Its escape hatch `HERMES_DESKTOP_CHILD_PID` (comma-separated pids, read from the UPDATER's own env) only protects the desktop's own backend when the desktop app itself runs the update — an independent portable Update.exe carries no such env, so updating one install KILLS another install's backend. Fixed in `Update.cs`: before `hermes update`, enumerate processes via PowerShell `Get-CimInstance Win32_Process` (wmic.exe is removed on Windows 11 24H2+), collect serve/dashboard pids whose ExecutablePath is NOT under the updated root (directory-boundary match `'<root>\*'`), and set `HERMES_DESKTOP_CHILD_PID` to them. C# must stay .NET Framework 4.0 csc-compatible (no `Contains(string, StringComparison)` — use `IndexOf(...) >= 0`). The Desktop-sync stage's root-wide pre-swap kill also excludes `Update.exe` by name (the updater was self-killing before its completion flow — step failures show the error MessageBox and return 1).

# Appendix: Prebuilt web/TUI bundles & build stamps (consolidated 2026-09-02 from SKILL.md)

## Contract
Ship prebuilt bundles so the packaged Portable NEVER runs a first-launch npm install:
- TUI: `hermes_cli/tui_dist/entry.js` (build: `npm install --workspace ui-tui --include=dev --silent --no-fund --no-audit --progress=false [--prefer-offline]` from the repo root, then `npm run build` inside `ui-tui/`, then copy `ui-tui/dist/entry.js`). A stale bundle breaks the TUI↔backend protocol — keep it in sync with ui-tui source via the packaging script (fatal) and the desktop-sync helper (non-fatal: on failure delete the stale bundle so the TUI falls back to first-launch install; gate the rebuild on the official `_tui_need_rebuild` mtime check, so an update that did not touch ui-tui skips it). The desktop-sync cleanup that removes repo `node_modules` is safe only while `tui_dist` exists.
- Web: `hermes_cli/web_dist` built from `web/` (vite `outDir: ../hermes_cli/web_dist` — no copy step). PIN the cwd with `Push-Location $Repo` for the INSTALL step too (`npm install --workspace web` resolves the workspace root from cwd; an unpinned cwd outside the repo fails with exit -4058 ENOENT).
- `web-ui-build-stamp.json` (`$HERMES_HOME`): content hash of web/ source, written by the SAME code that reads it — `hermes_cli.main._write_web_ui_build_stamp(repo, repo/'web')` via the staged venv (`$env:HERMES_HOME` + `PYTHONPATH` pointed at the 暂存源码) — otherwise `_web_ui_build_needed()` is True on first launch and `hermes dashboard` runs a runtime npm install. Fatal in the packaging script; non-fatal in the desktop-sync helper (never delete a good `web_dist` because the stamp failed).
- Add `-e hermes_cli/web_dist/` to every staged `git clean -fdx` (web_dist is gitignored upstream; mirror `-e hermes_cli/tui_dist/`).

## Stamp ordering (two failed builds 2026-08)
The stamp MUST be the last step that can change its hashed inputs (web/ + package.json + package-lock.json) — place it right before the archive step. TWO staged git resets exist in Hermes.ps1: the first (after copy) does NOT rewrite files (robocopy preserved mtimes, git's stat cache skips the rewrite, tree keeps CRLF); only the later `git rm -r --cached` + `reset --hard` (the line-ending fix) rewrites every tracked file to LF. A stamp written before that second reset hashes CRLF content and forces a first-launch rebuild. Same class: `desktop-build-stamp.json` (Hermes.ps1 `-Stage WriteDesktopStamp` and SyncDesktop after a successful build + PatchRemove) MUST be written AFTER the staged line-ending normalization — written before it, the stamp hashes CRLF bytes (verified `8cc565cc` vs `1082986a` for the same tree) and every update rebuilds the desktop. If upstream changes what feeds the desktop build, extend BOTH Get-DesktopContentHash and the official `_compute_desktop_content_hash`. TUI/Web bundles use the OFFICIAL judges (`_tui_need_rebuild` mtime, `_web_ui_build_needed` content-hash stamp); errors default to rebuild (conservative).
The `__pycache__` cleanup of the staged hermes-agent source tree MUST run AFTER the web UI build stamp step: the stamp step imports `hermes_cli` through the staged venv (PYTHONPATH points at the 源码) and re-creates `__pycache__` under the source tree, so a cleanup placed before it lets fresh .pyc files slip into the archive (observed 2026-08-05). Archive inventory check: `7za l` — the only remaining `__pycache__` entries must be under `runtime\python\...\Lib\` (CPython's own precompiled stdlib bytecode, normal); any entry under `data\hermes-home\hermes-agent\` means the cleanup ran too early.

## npm/electron operational facts
- Workspace hoisting is NOT guaranteed: as of 2026-08-10 the official root `package.json` no longer declares `electron` (it lives only in `apps/desktop` devDependencies, pinned 40.10.2), so `npm install --workspace apps/desktop` leaves it at `apps/desktop/node_modules/electron` and the root `node_modules/electron` does NOT exist — `Hermes.ps1` reads `$electronVersion` with a fallback lookup (root, else `apps\desktop\node_modules\electron\package.json`, throw if neither). Whenever upstream adds a Desktop dependency, a stale build-machine tree breaks the build with TS2307 (verified 2026-08-09: get-windows@9.3.0). npm hoists workspace deps to the ROOT `node_modules` — check `upstream/node_modules/<pkg>/`, not `apps/desktop/node_modules/`.
- `npm install --prefix <nodeDir> npm@<major>` (Ensure-OfficialNpm in Hermes.ps1 + its inline twin in Update-Portable.ps1 SyncDesktop) PRUNES the official Node zip's bundled `node_modules\corepack` (verified 2026-08-14 by reproduction). FIXED in BOTH scripts: read the bundled corepack version from `node_modules\corepack\package.json` BEFORE the npm upgrade, then after the upgrade reinstall exactly that version (`& $npmCmd install --prefix $NodeDir "corepack@$corepackVersion" --no-fund --no-audit --progress=false` when `dist\corepack.js` is missing). The version read from the unpacked tree always follows the official Node zip — no hardcoding. Regression gates in Verify-Portable.ps1: `$Checks.Corepack` (`node\node_modules\corepack\dist\corepack.js`) + `$Checks.CorepackShim` (`node\corepack.cmd`) existence, missing → exit 1.
- Offline-first caches (2026-08-09): `npm_config_cache`/`ELECTRON_CACHE`/`ELECTRON_BUILDER_CACHE` = `builder\assets\{npm-cache,electron-cache,electron-builder-cache}`; every workspace install uses `--prefer-offline`; the cache dirs ARE the assets dirs (first online build seeds them, later builds stay offline). Content-addressed — NEVER prune.
- Lockfile restore after the TUI/web steps: `& git.exe -C $Repo checkout -- package-lock.json` — explicit `-C` (the builds' Push-Location/Pop-Location pairs leave the script cwd OUTSIDE the repo; a bare `git checkout` fails "fatal: not a git repository"). In SyncDesktop the guard sits inside the TUI step's `try`, so a bare checkout failure makes the `catch` treat the whole step as failed and DELETE the freshly built bundle (false-positive "stale" removal) — first TUI launch regresses to npm install. Check BOTH `Hermes.ps1` and `Update-Portable.ps1` (SyncDesktop stage).
