---
name: deepseek-harness-portable-builder
description: Build dsh portable at D:\DeepSeek-Harness-Portable-Builder.
version: 1.0.0
author: hermes-agent
license: MIT
metadata:
  hermes:
    tags: [portable, dsh, deepseek-harness, windows, builder]
    related_skills: [hermes-agent-portable-builder]
---

# DeepSeek Harness Portable Builder

## When to Use

Use when the user asks to sync/build/package the DeepSeek Harness portable
(builder root `D:\DeepSeek-Harness-Portable-Builder`), rebuild after an
upstream release, or debug the DeepSeek Harness.exe launcher / portable layout.

Builds a relocatable Windows x64 portable of **DeepSeek Harness (dsh)** — the
pure-TypeScript agent harness (no Electron, no Python core). Product lives in
`D:\DeepSeek-Harness-Portable-Builder`; output zip lands in `dist\`.

## 经验写回 (Record lessons back into this skill)

Whenever you make a mistake while working on this project and then find the
correct approach, **update this skill immediately in the same session** — do
not wait to be asked, do not defer to a later task. This skill is the
project's institutional memory; every error-to-fix cycle that is not written
back is a lesson the next run will re-pay.

Trigger: you hit an error, a wrong assumption, a failed command, a user
correction, or a rework — AND you then found the approach that actually
worked. Typical examples from this project: the text-edit tool rewrites .cs
files WITHOUT the UTF-8 BOM that csc needs (re-add it before compiling;
the build's Ensure-Utf8Bom fixes it anyway — commit the BOM'd file);
`csc.exe` is a C# 5 compiler (no `out _` discards, no out-variable
declarations — declare variables first); PowerShell surfaces any native
stderr as NativeCommandError noise (judge by exit code, not the red text);
the Windows foreground lock refuses `Activate()` when another process owns
focus, so the second-double-click reveal needs ForceForeground
(AttachThreadInput + SetForegroundWindow).

How to record:

1. **Where**: add a dated pitfall (`observed YYYY-MM-DD`) to the `## Pitfalls`
   section of this SKILL.md. If it is a launcher/updater detail, also touch
   the matching contract section. Update this section with a short pointer
   line when the detail lives elsewhere.
2. **What**: symptom (exact error/behavior), root cause, the proven correct
   approach, and a one-line verification note. Name real files/line numbers
   where useful.
3. **Both copies**: this skill ships twice and MUST stay byte-identical —
   the 随包预置副本
   `D:\DeepSeek-Harness-Portable-Builder\builder\data\dsh\skills\deepseek-harness-portable-builder\SKILL.md`
   (the canonical editable copy, git-tracked in the builder repo) and the
   copy shipped inside the portable (the build's `Copy-Tree` of
   `builder\data` → `data` materializes it; also visible in
   `stage\...\data\dsh\skills\`). Edit the builder copy; after a build,
   verify `diff` against the stage copy reports IDENTICAL. If this skill is
   ever installed in an agent profile (e.g. the dsh portable's own embedded
   agent), sync that profile copy BACK to the builder copy after any patch —
   the builder copy is canonical (observed 2026-08-24: no profile copy
   exists on the build machine).
4. **Scope**: record only lessons that would save time if the same mistake
   recurs (project-specific, non-trivial, cost real time). Do not record
   one-off trivia, task progress, or anything stale in a week. When unsure
   whether a lesson is worth recording, record it — a concise pitfall is
   cheap; re-learning the mistake is not. When a pitfall's forensic narrative
   (dates, symptom→root-cause chains of an already-FIXED bug) grows longer
   than its actionable core, condense the narrative to the rule.
5. **Report**: in the final reply, state what was added and that both copies
   are in sync.

Portable layout (what ships):

```
DeepSeek-Harness-Portable\
├── DeepSeek Harness.exe  # C# winexe launcher: no console, tray icon, WebView2 app window (not a browser)
├── Microsoft.Web.WebView2.Core.dll / .WinForms.dll / WebView2Loader.dll  # WebView2 (Evergreen) assemblies, ~1.1 MB
├── node\          # portable Node v22.23.2 (zip from builder\assets\node)
├── app\           # @deepseek-ai/dsh installed with node-linker=hoisted (flat, symlink-free)
├── data\dsh\      # DSH_HOME: preinstalled skills; profiles/storages created on first run
└── README.txt
```

## Key facts

- dsh engines: `node ^22.19.0 || >=24.0.0` — bundled **v22.23.2** satisfies it.
- Web UI is a **static build** served by `dsh web` (default port 3080), not a dev server.
- User data redirects via **`DSH_HOME`** env var (falls back to `~/.dsh`).
- `profiles\node_modules` is a **symlink/junction farm** into `app\node_modules`,
  rebuilt by dsh on every boot (deleting it is safe self-healing).
- **pnpm node-linker=hoisted is MANDATORY**: default pnpm store uses absolute
  symlinks that break after archive/restore. hoisted = flat real directories.
- Python SDK (`python/sdk`) is Linux/macOS-only — do NOT bundle Python.

## Build (one command)

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; & 'D:\DeepSeek-Harness-Portable-Builder\builder\source\DeepSeek-Harness.ps1' *>&1 | Tee-Object -FilePath 'D:\DeepSeek-Harness-Portable-Builder\builder\logs\build-<stamp>.log'; if (-not $?) { Write-Host 'BUILD_FAILED (see log)'; exit 1 }; Write-Host 'BUILD_EXIT=0'; exit 0"
```

Run in background + notify_on_complete (build ≈ 3-5 min: pnpm install ~1.5 min + archive).
Script steps: assert upstream → wipe stage → Resolve-Node (**pinned v22.23.2**;
own assets → exact-URL download if missing) → Resolve-Pnpm (**pinned 11.21.0**,
builder cache only — never the system pnpm; bundled-npm install back-fills the
cache) → pnpm add @deepseek-ai/dsh@<ver> (hoisted + official registry +
allow-builds) → compile DeepSeek Harness.exe + Update.exe (csc winexe + icon;
UTF-8 BOM enforced on both .cs via Ensure-Utf8Bom; bundled pnpm.cjs ship is
asserted — the updater depends on it) → README
version injection → `dsh --version` via **node + lib\bin.js** (never the .cmd
shim) → **web probe (HTTP 200, dynamically allocated port)** →
**clear probe-generated data\dsh** → strip builder-path
app\node_modules\.modules.yaml (pnpm would refuse it) → 7za archive → `7za t`
verify.

## Sync upstream (every build)

```powershell
git -C "D:/DeepSeek-Harness-Portable-Builder/upstream" fetch --depth 1 --no-tags origin master
git -C "D:/DeepSeek-Harness-Portable-Builder/upstream" reset --hard origin/master
```

upstream = read-only mirror of `https://github.com/deepseek-ai/deepseek-harness.git`
— a **depth-1 SHALLOW mirror since 2026-08-23** (`.git` ~150MB+ → 19MB;
216MB → 83MB total). The build never needs history or tags — no
`git describe`/`--tags`/`rev-list` in DeepSeek-Harness.ps1, and the version
comes from `upstream\package.json`. Conversion recipe (verified in place):
`fetch --depth 1 --no-tags origin master` → `reset --hard origin/master` →
`remote remove origin` + re-add by URL (drops every origin/* ref keeping old
history reachable) → delete ALL local tags → `reflog expire --expire=now
--all` → `gc --prune=now --aggressive`. KEEP the shallow flags on every
sync: a plain `fetch --prune origin` would fetch every branch at full depth
and silently bloat the mirror again.

## Pitfalls

- **Node version must be pinned, not "latest"**: `Resolve-Node` previously
  scraped the `latest-v22.x` index and could silently ship a newer 22.x than
  the one the README/launcher advertise. Now pinned to **v22.23.2** with a
  direct `dist/v22.23.2/` download URL; the cache filter matches that exact
  name. Keep the pin in sync with the launcher contract and dsh engines.
- **pnpm must be pinned and never taken from the system**: `Resolve-Pnpm`
  uses **only the builder's own cache** — the system global pnpm is never
  consulted (previously a system pnpm 11.x was preferred, which
  made builds depend on what the build machine happened to have installed).
  Pinned to **11.21.0** (`$PnpmVersion`): `--config.dangerously-allow-all-builds`
  is pnpm-11 syntax and older majors silently ignore it (→
  `ERR_PNPM_IGNORED_BUILDS`). First build installs the pin with the bundled
  npm and back-fills `builder\assets\pnpm\`; later builds restore from cache
  fully offline. Keep the pin in sync with README.
- **pnpm 11 blocks dep install scripts by default** → exit 1 with
  `ERR_PNPM_IGNORED_BUILDS` even though packages installed. MUST pass
  `--config.dangerously-allow-all-builds` (node-pty/koffi native modules).
- **Registry mirror breaks install**: a user-level registry (npmmirror) makes
  cross-platform optional tarballs fail with `UND_ERR_DESTROYED`. Pin
  `--registry=https://registry.npmjs.org/` explicitly in the script.
- **node.exe cannot run `.cmd` shims**: dsh.cmd is a cmd wrapper — launch
  `app\node_modules\@deepseek-ai\dsh\lib\bin.js` directly (both in the build
  probe/version check and in DeepSeek-Harness.cs). The build now runs
  `dsh --version` through `node <bin.js>` too, not `dsh.cmd`.
- **README.txt CLI hint must also point at bin.js**: the shipped "命令行方式"
  line used to read `app\node_modules\.bin\dsh --help` — a shell shim node.exe
  cannot execute. Fixed to `app\node_modules\@deepseek-ai\dsh\lib\bin.js
  --help`.
- **7za writes progress to stderr** → under `$ErrorActionPreference='Stop'`
  surfaces as NativeCommandError on SUCCESS. Run archive+verify with EAP
  relaxed, judge by `$LASTEXITCODE` only; then `7za t` the archive (a killed
  7za leaves a truncated zip that `a` reports as success).
- **Probe-generated `data\dsh\profiles\node_modules` is a junction farm that
  MUST NOT ship**: 7za follows junctions into the archive (400MB+ bloat; the
  extracted real tree then trips dsh's `healProfilesModuleFallback`
  "exists and is not a symlink" error on boot). Clear `data\dsh\*` BEFORE
  (`Remove-Item -Recurse` is unreliable on junction trees → fall back
  to `subst` + `cmd /d /c rd /s /q`). Launcher self-heals on start anyway.
- **Test-generated `data\webview2` must not ship either**: a manual launcher
  run inside the stage (e.g. testing the WebView2 window) leaves the full
  EBWebView profile — History/Cache/GPUCache/Local Storage/window-state.ini,
  hundreds of files including the tester's browsing data. The pre-archive
  cleanup wipes the whole `data\webview2` dir (no preinstalled content lives
  there; the launcher recreates it on first start).
- **Stale node/DeepSeek Harness processes hold the stage**: before rebuild, kill
  `node.exe` + `DeepSeek Harness.exe` (a shell whose cwd sits inside stage also blocks
  deletion; `Remove-Item` from PowerShell with an explicit path works when
  bash rm fails). The web probe now also **kills its whole process tree**
  (`taskkill /PID <pid> /T /F`) instead of only the main PID, so a surviving
  child node cannot lock stage files during archiving.
- **Probe port must be dynamic**: a fixed probe port (was 34567) fails the
  build when that port is already taken on the build machine. `Get-FreePort`
  binds port 0 on loopback and uses the OS-assigned port.
- **`npm install` on this dep tree is pathologically slow** (dependency
  graph is huge): use pnpm (≈30s vs npm 10+ min hang). Never wait on npm.
- `pnpm config get node-linker` returns undefined even with `.npmrc` — pass
  `--config.node-linker=hoisted` on the command line.
- **The hoisted tree ships `app\node_modules\.modules.yaml` recording builder
  paths** (storeDir/virtualStoreDir point into the builder's stage). pnpm
  refuses to update such a tree, so the build strips it from the zip and
  Update.exe deletes it before every install.
- **A marquee-only progress dialog reads as "stuck"**: the old Update.exe
  showed an indeterminate bar with zero feedback while npm ground for 6+
  minutes. The updater now streams pnpm's live output lines + elapsed seconds
  into the dialog and the diagnostic log.
- **`dsh web` opens the default browser by itself**: upstream `web-app`
  defaults `openBrowser: true` and pops the browser on service-ready. Both the
  launcher (`DeepSeek-Harness.cs`) and the build web probe
  (`DeepSeek-Harness.ps1`) MUST pass `--no-open` — otherwise the URL opens
  twice (WebView2 window + dsh) and the build pops a browser on the build
  machine. The launcher is the single owner of the UI handoff.
- **Two same-path instances must never share `data\webview2`**: WebView2
  refuses a user data folder already in use by another process, so the
  per-path named mutex (`DeepSeekHarnessPortable_Mutex_<fnv1a8>`,
  `WaitOne(0)`, `AbandonedMutexException` → take over) is what stops a second
  launcher of the same copy — the random-port fallback must NEVER be used for
  the same path (would corrupt/conflict on the shared data dirs), only for
  different-path copies. When editing the launcher keep the mutex and the
  reveal event scoped to the path hash; the reveal event was previously the
  global `DeepSeekHarnessPortable_Show` (pre-2026-08-23) and would have made
  one copy's double-click raise EVERY copy's window.
- **Update.exe 的 marker 只能由声称者删除（observed 2026-08-24）**:
  `MainBody` 的 finally 曾无条件删除 `.dsh-update-in-progress`——更新进行中
  打开一个 `--check` 窗口再关掉，会把正在更新的 marker 删掉，第三个
  Update.exe 就能并发安装（两个 pnpm 写同一个 `app\`）。修复：只在
  `markerClaimed` 时删除。同类问题：进程归属匹配必须用
  `StartsWith(root + "\\")`，裸 `StartsWith(root)` 会把 `D:\portable2` 误判为
  `D:\portable` 的实例（node/launcher 都会被误杀）。启动器侧无此问题
  （精确路径比较 + 路径哈希命名）。
- **探针脚本必须 100% ASCII（observed 2026-08-24）**:
  本环境把无 BOM 的 .ps1 按 ANSI/GBK 解码，探针脚本里任何中文（含注释）都可能
  破坏语法（乱码字节可含 0x27）——探针脚本必须 100% ASCII，中文用码点拼
  （`[char]0xXXXX`）。
- **兜底 clone 提示必须 shallow（fixed 2026-08-26）**:
  `Assert-Upstream` 在 `upstream\` 缺失时 throw 的提示命令曾是裸
  `git clone <official>`——全量克隆会拉下 ~150MB+ 历史（镜像策略自
  2026-08-23 起是 depth-1 shallow，同步用 `fetch --depth 1 --no-tags
  origin master`）。修复：提示改为 `git clone --depth 1 --no-tags
  --branch master <official> <repo>`，与镜像策略一致。教训：任何"缺失时
  重建"的提示/命令都必须带上与既定同步策略相同的 shallow 标志，否则会
  静默撑大镜像。
- **更新完成「是否立即重启」对话框（fixed 2026-08-26）**: 成功路径的 `Close()` 曾**无条件执行**
  ——点「否」也会关闭更新器窗口（用户要求：是→重启并关闭；否→保持窗口打开）。且成功路径
  从未释放 `_busy`（点否后按 X 关窗会弹「更新正在进行中…确定要关闭吗？」，与 Hermes updater
  2026-08-26 同类坑）。修复：`SetBusy(false)` 移到分支之前（按钮随后显式禁用），`Close()` 移进
  `rr == DialogResult.Yes` 分支内；「否」时窗口保持打开。
- **重复「检查更新」日志叠加（fixed 2026-08-26）**: Update.cs 的 `CheckForUpdates()`/`RunUpdate()`
  每次都 `AppendLog` 追加输出，连点两次检查更新会把上一次的结果/输出叠在下面（用户反馈：
  日志会叠加）。修复：每次开始新的检查/更新时先 `_txtLog.Clear()`——`CheckForUpdates` 在
  `SetStatus("正在检查更新...")` 之后、`RunUpdate` 在 busy 守卫之后各加一行。两个方法都在
  UI 线程执行（点击 / `BeginInvoke`），直接 Clear 无竞态：按钮 busy 期间禁用，前一轮输出在
  完成回调（UI 线程）中已全部落盘，不存在排队中的 Append 追尾。Hermes updater 2026-08-26
  同类修复。

## Launcher (DeepSeek-Harness.cs) contract

- winexe via `csc /target:winexe /win32icon:<ico>` — no console window.
- Sets `DSH_HOME` to `<root>\data\dsh`, prepends `<root>\node` to PATH.
- Deletes `data\dsh\profiles\node_modules` on start (self-heal after move). Kills the dsh web process TREE (taskkill /T /F) on exit/退出 so no orphaned workers remain.
- **Single instance PER PATH, multi-instance ACROSS paths**: single-instance is
  enforced by a named mutex derived from the portable root
  (`DeepSeekHarnessPortable_Mutex_<fnv1a8>`), NOT by the port — a second
  launcher of the SAME copy never starts: it signals the running instance to
  show its window (per-path named event `DeepSeekHarnessPortable_Show_<fnv1a8>`,
  ForceForeground = AttachThreadInput + SetForegroundWindow, since plain
  `Activate()` is refused when another process owns the foreground lock) and
  exits (no second node process, no second tray). An abandoned mutex (previous
  instance crashed) is taken over via the `AbandonedMutexException` catch, so
  a crash never blocks the next launch. The reason the mutex is per-PATH:
  same path = same `data\dsh` + `data\webview2`, and two processes must never
  share the WebView2 user data folder (second process fails to init). Copies
  launched from OTHER paths are independent instances (their own data dirs).
- **Port 3080 canonical, random-port fallback for other copies**: the first
  instance of any copy boots `dsh web --no-open --port 3080` when 3080 is
  free. If 3080 is already LISTENING (a different-path copy or a foreign
  program), the launcher does NOT fail and does NOT hijack the running
  instance — it falls back to an OS-assigned random port via `GetFreePort()`
  (bind port 0 on loopback, read the assigned port, release — node's `--port 0`
  semantics, but resolved IN THE LAUNCHER because dsh web must be told the
  concrete port and the launcher needs the URL for the app window). So N
  copies at N different paths run side by side on N ports (first one owns
  3080). `IsAppUrl` compares against the ACTUAL `_port`, not a hard-coded
  3080, so external-link interception still works on the fallback port.
- **WebView2 app window (not a browser)**: after HTTP 200 the launcher hosts
  the dsh web UI in a WebView2 WinForms window (Evergreen mode — uses the
  system WebView2 Runtime; ships only Core/WinForms DLLs + WebView2Loader,
  ~1.1 MB). Window title fixed "DeepSeek Harness" with the DeepSeek icon;
  close = hide to tray (tray 退出 is the only exit); first-run default window
  size from `DeepSeek-Harness.cs` `Width`/`Height` (docs do not hard-code the
  default), the user's own size
  is remembered from then on in `data\webview2\window-state.ini`
  (`[Window] width/height`); external links
  and `target=_blank` open in the system default browser (NavigationStarting /
  NewWindowRequested interception); WebView2 data under `data\webview2`
  (portable). Missing Runtime → dialog guiding to the official download page,
  then browser fallback.
- Icon: `builder\source\DeepSeek-Harness.ico` is a COMMITTED asset (generated
  once from upstream `apps/web/public/favicon.svg` via sharp → PNG 256 → PIL
  multi-size ICO; the build itself never regenerates it). The build embeds it
  via `/win32icon:`; the tray and window title bar load it via
  ExtractAssociatedIcon.

## In-place updater (Update.exe)

`builder\source\Update.cs` compiles to `Update.exe`
(winexe + icon) at the portable root. It updates dsh WITHOUT rebuilding:

- **Window UI, no auto-check**: launching Update.exe shows a window
  (当前版本 / 最新版本 / 状态,检查更新 / 立即更新 buttons, plus a
  live log box; 关闭 uses the window's top-right X). Nothing is queried until 检查更新 is clicked. `--check`
  (launcher tray "检查更新") opens the same window and runs one check on
  load, so it stays usable while the app is open.
- Uses the **bundled pnpm** (`node\node_modules\pnpm\bin\pnpm.cjs` — ships in
  every build; ~30s installs vs npm's 10+ min hang on the huge dsh dep tree).
  Falls back to the bundled npm only when pnpm is missing (very old portables).
- Check (`npm view @deepseek-ai/dsh version` with `--fetch-timeout=15000
  --fetch-retries=2`) renders in the window: 发现新版本 / 已是最新版本 /
  无法查询 registry(离线或代理异常)。The update runs inside the same window:
  `pnpm add @deepseek-ai/dsh@latest --registry=https://registry.npmjs.org/
  --config.node-linker=hoisted --config.dangerously-allow-all-builds
  --fetch-retries=5 --network-concurrency=8 --config.minimum-release-age=0` (npm fallback: `npm install ...
  --no-audit --no-fund --fetch-retries=5`) with a live streaming log box
  (real-time output; the log IS the progress view, no marquee bar); verifies via `bin.js --version`; **on success asks 是否立即重启 (MessageBox Yes/No) — relaunches DeepSeek Harness.exe on 是, and the launcher then shows the WebView2 window**; user data `data\dsh`
  untouched.
- **pnpm blocks fresh releases (minimumReleaseAge)**: pnpm 11's supply-chain policy refuses packages younger than 1 day (default `minimum-release-age=1440`). A just-published dsh makes `pnpm add @deepseek-ai/dsh@latest` silently resolve @latest to the previous version and print "Done" without changing anything (rc.2 stayed rc.1, version guard fired 更新失败). Both the build and Update.exe pass `--config.minimum-release-age=0`.
- **pnpm prints "Done" but can linger**: the pnpm process tree may keep running after "Done in Xs using pnpm" (post-run network chatter / a lingering child — Update.exe waited forever on a flaky proxy). The updater detects the "Done in ... using pnpm" marker, kills the tree immediately (no grace) once the marker is seen — pnpm normally exits ~0.1s after Done, but may linger forever; so the instant it has not exited after Done, it is killed, and then kills the whole tree (taskkill /T /F) and proceeds — the `bin.js --version` check is the real gate. No hard timeout is needed: the stream either exits normally, hits a non-zero exit (shown as 更新失败), or is grace-killed after the marker (the post-install `bin.js --version` check is the gate).
- **Pre-flight network probe + failure classification**: before the install,
  Update.exe probes registry.npmjs.org through the SAME bundled node the
  installer uses (https.get, 6s timeout — a .NET HttpWebRequest probe
  misjudges this machine's TLS path). A dead/flaky link fails in
  seconds with a clear reason (超时/DNS/拒绝/代理) instead of minutes of pnpm
  retries. Install/check failures are classified from the raw output (网络 /
  DNS / 403 权限) into user-facing causes, mirroring the Hermes updater's
  ClassifyUpdateError.
- **`add`, never `install`**: `pnpm install <spec>` silently reinstalls the
  existing spec and leaves the tree at the old version — the
  updater uses `pnpm add`. A post-install version check against the registry's
  "latest" fails loudly if the tree did not actually change.
- **Stale `.modules.yaml` blocks pnpm**: the hoisted tree ships
  `app\node_modules\.modules.yaml` whose storeDir/virtualStoreDir record the
  BUILDER machine's paths; pnpm refuses to work on it ("dependencies are
  currently symlinked from the virtual store..."). Update.exe deletes it
  before every install (hoisted trees do not need it) and the build strips it
  from new zips.
- **Install failure is never silent**: a non-zero exit, a process-start
  failure, a missing `bin.js`, or a version mismatch shows 更新失败 in the
  window with the full output in the log box — the earlier code
  treated a null capture as success and could show a fake 更新完成 with the
  OLD version still installed (the user's real failure mode: 6+ min marquee
  bar, then nothing).
- **Version comparison normalizes caret ranges**: pnpm writes
  `"@deepseek-ai/dsh": "^0.1.1-rc.2"` into package.json; Update.exe strips a
  leading `^~><=v` before comparing so a fresh install is not reported as
  "updatable" forever.
- **`npm view` failure is not a version**: RunCapture appends `[stderr]` when
  npm exits non-zero (e.g. offline); the version parse cuts at that marker so
  an error message is never offered as "latest". Offline
  still shows 无法查询 registry in the window.
- Re-entrancy marker `data\dsh\.dsh-update-in-progress` (PID, stale-safe; `--check` never claims it — read-only, so tray checks work while the window is open);
  automatically stops this portable's own launcher/web processes before updating (taskkill /T /F — the tray icon disappears with the launcher); instances from other directories are never touched; diagnostic log
  `data\dsh\logs\Update-exe-diagnostic.log`.
- Launcher tray menu: 打开界面 / **打开网页** (opens the same UI in the
  system default browser — useful when the WebView2 Runtime is missing or the
  user prefers a real browser tab) / 检查更新 (spawns Update.exe --check)
  / 退出.
- C# gotchas: MessageBox.Show returns `DialogResult` (declare `DialogResult
  r =`); write the .cs with UTF-8 BOM (PowerShell 5.1 ANSI-mangles CJK in
  strings otherwise; the build enforces it via Ensure-Utf8Bom); quote
  `"@deepseek-ai/dsh"` in npm/pnpm args; the old MessageBox-driven flow was
  replaced by the window UI.

## Assets (self-contained builder)

The builder is **fully offline-self-contained**: everything it needs lives
under `builder\assets`, and any missing asset is downloaded once and
back-filled (no cross-builder fallback):

- `builder\assets\7zip\7za.exe` — 7-Zip CLI; missing → download `7zr.exe` +
  `7z2602-extra.7z` from 7-zip.org and unpack `7za.exe` out of it.
- `builder\assets\node\node-v22.23.2-win-x64.zip` — pinned portable Node;
  missing → download the pinned URL and back-fill.
- `builder\assets\git\` — **PortableGit 2.55.0.3**, cached UNPACKED as
  `builder\assets\git\PortableGit\cmd\git.exe` (no archive kept, zero
  per-build extraction): the build uses it for upstream reads
  (`Assert-Upstream` status/rev-parse) and **never consults the system git**
  (previously the build machine had to have any git
  preinstalled). Missing → download the pinned release, extract to temp, and
  back-fill the unpacked cache dir.
- `builder\assets\pnpm\` — cached **pnpm@11.21.0 install** (pinned): the `pnpm`
  package dir (self-contained, all deps bundled inside `node_modules\pnpm`)
  plus the `pnpm.cmd`/`pnpm.ps1`/`pnpx.*` shims. `Resolve-Pnpm` **never uses
  the system pnpm** — it restores from this cache, or (first build) installs
  the pin with the bundled npm and back-fills the cache. pnpm is therefore
  NOT required on the build machine, and an offline builder works after one
  online build.

## 随包预置 (builder\data)

`builder\data` mirrors the deployed `data\` layout and is copied as-is into
the stage (`Copy-Tree $BuilderData $Stage\data` during assembly) — adding a
file under `builder\data` ships it. Currently ships
`data\dsh\skills\deepseek-harness-portable-builder\SKILL.md` (rank-400
user-dsh discovery root; the agent can maintain its own builder from inside
the portable). The pre-archive
probe cleanup is **whitelist-scoped** to `profiles`/`storages` only —
preinstalled skills survive the cleanup (earlier the
cleanup wiped everything under `data\dsh`).

## This skill's canonical copy and shipped copy — keep them byte-identical

The canonical (editable) copy is the git-tracked
`builder\data\dsh\skills\deepseek-harness-portable-builder\SKILL.md`; every
edit and every 经验写回 patch goes there (observed 2026-08-24: there is NO
profile-master copy in the active agent profile on the build machine — the
old "profile master" wording was fiction; git history shows all edits
landing in the builder copy).

The build's `Copy-Tree` (`builder\data` → `data`) ships it into every
portable, so the shipped copy is generated, not hand-synced. After a build,
verify the stage copy is byte-identical: `diff` the builder copy against
`stage\DeepSeek-Harness-Portable\data\dsh\skills\...\SKILL.md` — a divergence
here means a build shipped stale skill text.

**Ordering contract: update the skill BEFORE building/packaging** (observed
2026-08-26: a lesson was written back to the skill AFTER the ZIP was
archived, so the shipped skill was stale). The build's `Copy-Tree` runs at
mid-build; a skill edited after that copy never reaches the artifact unless
you re-sync manually. Sequence for any skill change that must ship:
(1) patch the builder copy, (2) verify the stage copy is byte-identical or
`cp` it over, (3) only then archive. After archiving, verify the ZIP's
extracted SKILL.md md5 equals the builder copy — do not assume the archive
picked up the edit.

If this skill is ever installed into an agent profile (e.g. the dsh
portable's own embedded agent maintaining its builder from inside the
portable), that profile copy is a THIRD materialization: after any patch to
the builder copy, sync the profile copy FROM it and verify `diff` IDENTICAL.
Rule: no patch is done until every existing copy matches.

术语约定: 两份副本或本地与远端内容不一致，一律称「差异」(divergence)，
不要用「漂移」(drift) 之类的说法。

## Verify a release

Extract to a fresh temp dir, then:
1. `node\node.exe app\node_modules\@deepseek-ai\dsh\lib\bin.js --version` → version
2. Run `DeepSeek Harness.exe`, poll `http://127.0.0.1:3080/` → HTTP 200; a WebView2
   app window appears (title "DeepSeek Harness", DeepSeek icon in the title bar),
   `data\webview2\EBWebView` is created, and NO default-browser process is
   spawned (the launcher owns the UI handoff).
3. WebView2 assemblies ship beside the launcher: `Microsoft.Web.WebView2.Core.dll`,
   `Microsoft.Web.WebView2.WinForms.dll`, `WebView2Loader.dll` present at the
   portable root.
4. `data\dsh\profiles\node_modules\@deepseek-ai` → ~195 junction entries (self-healed)
5. Icon extractable from DeepSeek Harness.exe (32x32).
6. `7za t` the zip: "Everything is Ok"; `data\dsh` contains ONLY preinstalled
   content (`skills\...` + dsh's own web profile scaffold) — no
   probe-generated junction farm (`profiles\node_modules`),
   no `storages`, and no `data\webview2` (test-run residue is wiped pre-archive).
Update.exe smoke test (verify any release with these too):
7. Launch Update.exe (plain): a window opens with 检查更新 / 立即更新 buttons
   — confirms this is the window UI build, not the old MessageBox flow.
8. Launch Update.exe (plain) and confirm the process is still alive after 4s
   (window constructed without crashing), then taskkill /F it. The stale
   .dsh-update-in-progress marker is PID-checked and ignored on next run.
9. Headless end-to-end of the update command chain in a COPY of the portable
   (never the live one): copy `app\` to a temp dir, delete
   `node_modules\.modules.yaml`, then in the copy run
   `node\node.exe node\node_modules\pnpm\bin\pnpm.cjs add @deepseek-ai/dsh@latest
   --registry=https://registry.npmjs.org/ --config.node-linker=hoisted
   --config.dangerously-allow-all-builds --fetch-retries=5
   --network-concurrency=8` → expect exit 0, package.json dep AND
   `bin.js --version` both equal the registry latest (e.g. 0.1.1-rc.2), and
   data\dsh untouched.
10. `Update.exe --check` (tray path) opens the window and runs one check on
    load — GUI-only; verify manually once per release.
Window behaviour (verify once per release, manually):
11. First run: the window opens at the default size (`DeepSeek-Harness.cs`
    `Width`/`Height`). Resize the window →
    close (hide to tray) → relaunch from tray 打开界面 → the size is restored
    from `data\webview2\window-state.ini`.
12. A second double-click of `DeepSeek Harness.exe` while running only
    re-shows the existing window (single-instance reveal) AND brings it to the
    foreground (ForceForeground), no second process.
13. Multi-instance across paths: extract the zip to TWO different temp dirs
    and launch both. The first copy owns 3080; the second (3080 busy) boots on
    a random port — poll `netstat -ano` for two `node.exe` LISTEN entries (one
    on 3080, one on a random port), both answer HTTP 200, and both trays exist.
    Then double-click copy 2's exe again → no third node process, copy 2's
    window is revealed. Kill both trees (taskkill /T /F on each launcher PID)
    and delete the temp dirs when done.
14. Clicking an external link in the UI opens the system default browser,
    not a navigation inside the app window.
