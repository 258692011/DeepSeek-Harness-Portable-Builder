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

Portable layout (what ships):

```
DeepSeek-Harness-Portable\
├── DeepSeek Harness.exe  # C# winexe launcher: no console window, tray icon, opens browser
├── node\          # portable Node v22.23.2 (zip from builder\assets\node)
├── app\           # @deepseek-ai/dsh installed with node-linker=hoisted (flat, symlink-free)
├── data\dsh\      # DSH_HOME: profiles/storages, created on first run (empty in release)
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
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; & 'D:\DeepSeek-Harness-Portable-Builder\builder\source\DeepSeek-Harness.ps1' *>&1 | Tee-Object -FilePath 'D:\DeepSeek-Harness-Portable-Builder\builder\logs\build-<stamp>.log'"
```

Run in background + notify_on_complete (build ≈ 2-4 min: install 30s + archive).
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
git -C "D:/DeepSeek-Harness-Portable-Builder/upstream" fetch --prune origin
git -C "D:/DeepSeek-Harness-Portable-Builder/upstream" reset --hard origin/master
```

upstream = read-only mirror of `https://github.com/deepseek-ai/deepseek-harness.git`.

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
  archiving (Remove-Item -Recurse is unreliable on junction trees → fall back
  to `subst` + `cmd /d /c rd /s /q`). Launcher self-heals on start anyway.
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
  twice (launcher + dsh) and the build pops a browser on the build machine.

## Launcher (DeepSeek-Harness.cs) contract

- winexe via `csc /target:winexe /win32icon:<ico>` — no console window.
- Sets `DSH_HOME` to `<root>\data\dsh`, prepends `<root>\node` to PATH.
- Deletes `data\dsh\profiles\node_modules` on start (self-heal after move). Kills the dsh web process TREE (taskkill /T /F) on exit/退出 so no orphaned workers remain.
- **Port 3080 is canonical and single-instance**: if 3080 is already
  LISTENING (a previous instance is running), the launcher does NOT start a
  second server — it waits briefly for HTTP 200, opens
  `http://127.0.0.1:3080/` directly and exits (no second node process, no
  second tray). Only when 3080 is free does it boot
  `dsh web --no-open --port 3080`. No ephemeral-port fallback (a second
  double-click must never land on a random port).
- Waits for HTTP 200, opens default browser, tray icon with 打开/退出.
- Icon: upstream `apps/web/public/favicon.svg` (official DeepSeek whale) →
  sharp (from app node_modules) → PNG 256 → PIL multi-size ICO →
  `builder\source\DeepSeek-Harness.ico`.

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
  (real-time output; the log IS the progress view, no marquee bar); verifies via `bin.js --version`; **asks 是否立即重启 (MessageBox Yes/No) on success — relaunches on 是, and the launcher then opens the browser**;
  **auto-relaunches DeepSeek Harness.exe** on success; user data `data\dsh`
  untouched.
- **pnpm blocks fresh releases (minimumReleaseAge)**: pnpm 11's supply-chain policy refuses packages younger than 1 day (default `minimum-release-age=1440`). A just-published dsh makes `pnpm add @deepseek-ai/dsh@latest` silently resolve @latest to the previous version and print "Done" without changing anything (rc.2 stayed rc.1, version guard fired 更新失败). Both the build and Update.exe pass `--config.minimum-release-age=0`.- **pnpm prints "Done" but can linger**: the pnpm process tree may keep running after "Done in Xs using pnpm" (post-run network chatter / a lingering child — Update.exe waited forever on a flaky proxy). The updater detects the "Done in ... using pnpm" marker, kills the tree immediately (no grace) once the marker is seen — pnpm normally exits ~0.1s after Done, but may linger forever; so the instant it has not exited after Done, it is killed, and then kills the whole tree (taskkill /T /F) and proceeds — the `bin.js --version` check is the real gate. No hard timeout is needed: the stream either exits normally, hits a non-zero exit (shown as 更新失败), or is grace-killed after the marker (the post-install `bin.js --version` check is the gate).
- **Pre-flight network probe + failure classification**: before the install,
  Update.exe probes registry.npmjs.org through the SAME bundled node the
  installer uses (https.get, 6s timeout — a .NET HttpWebRequest probe
  misjudges this machine's TLS path). A dead/flaky link fails in
  seconds with a clear reason (超时/DNS/拒绝/代理) instead of minutes of pnpm
  retries. Install/check failures are classified from the raw output (网络 /
  DNS / 403 权限) into user-facing causes, mirroring the Hermes updater's
  ClassifyUpdateError.- **`add`, never `install`**: `pnpm install <spec>` silently reinstalls the
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
- Launcher tray menu: 打开界面 / **检查更新** (spawns Update.exe --check)
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
  back-fill the unpacked cache dir.- `builder\assets\pnpm\` — cached **pnpm@11.21.0 install** (pinned): the `pnpm`
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
the portable) and `data\dsh\profiles\web\cordis.patch.yml` (deployment
persona — Simplified-Chinese reply rule for every mode). The pre-archive
probe cleanup is **whitelist-scoped** to `profiles`/`storages` only —
preinstalled skills and profile patches survive the cleanup (earlier the
cleanup wiped everything under `data\dsh`).

## This skill lives in TWO places — keep them byte-identical

THIS SKILL FILE has two copies and they MUST stay byte-identical:

1. **profile master** (the editable one): the copy under the active agent
   profile's skills directory — this is the file `skill_manage` writes to,
   i.e. the very skill being read right now.
2. **随包预置副本** (ships into the portable):
   `D:\DeepSeek-Harness-Portable-Builder\builder\data\dsh\skills\deepseek-harness-portable-builder\SKILL.md`

Every edit via `skill_manage patch` only touches copy #1. **After any patch,
copy it over the preinstall copy**: `cp <profile>/SKILL.md <builder-data>/SKILL.md`,
then verify `diff` reports IDENTICAL (the preinstall copy silently
drifted to a stale 153-line copy while the profile grew to 164 lines — a build
would have shipped the old text). Rule: no patch is done until both copies match.

## Verify a release

Extract to a fresh temp dir, then:
1. `node\node.exe app\node_modules\@deepseek-ai\dsh\lib\bin.js --version` → version
2. Run `DeepSeek Harness.exe`, poll `http://127.0.0.1:3080/` → HTTP 200, `<title>DeepSeek Harness</title>`
3. `data\dsh\profiles\node_modules\@deepseek-ai` → ~195 junction entries (self-healed)
4. Icon extractable from DeepSeek Harness.exe (32x32).
5. `7za t` the zip: "Everything is Ok"; `data\dsh` contains ONLY preinstalled
   content (`profiles\web\cordis.patch.yml` + the official scaffold files,
   `skills\...`) — no probe-generated junction farm (`profiles\node_modules`)
   and no `storages`.
Update.exe smoke test (verify any release with these too):
6. Update.exe at the portable root is the WINDOW build: its UTF-16 strings
   contain 检查更新 / 立即更新 / 发现新版本 (window UI — the old
   MessageBox-driven flow is gone).
7. Launch Update.exe (plain) and confirm the process is still alive after 4s
   (window constructed without crashing), then taskkill /F it. The stale
   .dsh-update-in-progress marker is PID-checked and ignored on next run.
8. Headless end-to-end of the update command chain in a COPY of the portable
   (never the live one): copy 
`app\`
 to a temp dir, delete
   
`node_modules\.modules.yaml`
, then in the copy run
   
`node\node.exe node\node_modules\pnpm\bin\pnpm.cjs add @deepseek-ai/dsh@latest
   --registry=https://registry.npmjs.org/ --config.node-linker=hoisted
   --config.dangerously-allow-all-builds --fetch-retries=5
   --network-concurrency=8`
 → expect exit 0, package.json dep AND
   
`bin.js --version`
 both equal the registry latest (e.g. 0.1.1-rc.2), and
   data\dsh untouched.
9. 
`Update.exe --check`
 (tray path) opens the window and runs one check on
   load — GUI-only; verify manually once per release.
