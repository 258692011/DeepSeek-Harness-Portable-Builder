---
name: hermes-agent-portable-builder
description: "Build relocatable Electron apps with bundled backends."
version: 1.0.0
author: Hermes Agent
license: MIT
platforms: [windows]
metadata:
  hermes:
    tags: [electron, packaging, portable, backend, windows]
    category: software-development
---

# Hermes Portable Builder Skill

Use this skill to research or implement relocatable Electron distributions, especially applications that launch a Python or other native backend. It distinguishes a packaged Electron shell from a genuinely self-contained portable application and emphasizes runtime path resolution, writable-state isolation, native dependency staging, and clean-machine verification.

This is a user-project skill for the Hermes Portable Builder, not an official bundled Hermes skill. Its canonical skill source is `D:\Hermes-Agent-Portable-Builder\builder\data\hermes-home\skills\software-development\hermes-agent-portable-builder`, while build-only implementation lives separately under `builder`. Releases install this skill tree under `<portable-root>\data\hermes-home\skills\software-development\hermes-agent-portable-builder`; executable maintenance helpers live once under `<portable-root>\scripts`.

术语约定: 两份副本或本地与远端内容不一致，一律称「差异」(divergence)，不要用「漂移」(drift) 之类的说法。正文一律写**完整明确的名称**（技能名/仓库路径/文件/组件名），**禁止口语化即席指代**（如「…侧」「那边」这类未定义说法；observed 2026-09-04：「deepseek 侧」曾在本技能正文出现、难以理解，已全部改为完整名称并全文清零）。

## Project Release Contract

- **hermes update DLL-lock 前置**（两类，均 FIXED in Update.cs）：(1) 外部进程锁——Windows 拒绝替换被运行中进程加载的 DLL（`cryptography/_rust.pyd` os error 5），`StopPortableProcesses(root)`（按 root 目录边界过滤停 Hermes/python）在官方 update 前与 Repair `-UpdatePython` 前各执行一次解锁；(2) SELF-LOCK——hermes update 进程自己锁 venv（`jiter.cp311-win_amd64.pyd` os error 5），robocopy `/MIR` site-packages 副本 + `HERMES_PORTABLE_SITE_PACKAGES` 指向副本解决（`hermes-cli.cmd` 用 `if not defined` 尊重外部预置）。hermes update 失败**不重试**（2026-08-15 用户决定）。症状链见 references「hermes update failure classes」。
- **入口三件套**：`hermes-tui.cmd`/`hermes-dashboard.cmd` 是 `hermes-cli.cmd` 的薄 CRLF wrapper（dashboard 解析 `--port`，默认 9119，端口已占则只开浏览器）。PITFALL: `shift` 覆盖 `%0`——参数循环前先 `set "BIN=%~dp0"`。环境逻辑只放 `hermes-cli.cmd`（清除桌面泄漏的 `HERMES_WEB_DIST`，否则独立 `hermes dashboard` 会 serve Electron 桌面包）。三个入口 + README.txt 故障排查条目一起发布。
- **Prebuilt bundle 契约**（TUI `hermes_cli/tui_dist/entry.js` + web `hermes_cli/web_dist`）：打包两者，否则首次启动跑运行时 npm install。构建与 stamp 细节（web stamp 必须由读取方同一代码 `hermes_cli.main._write_web_ui_build_stamp` 经 staged venv 写入、且是最后一步能改 hashed inputs 的步骤；staged `git clean -fdx` 排除 `-e hermes_cli/web_dist/`）见 references「Prebuilt web/TUI bundles & build stamps」。构建脚本 fatal；desktop-sync helper 非 fatal（失败删 stale bundle 回退首启安装）。
- **版本选择权威**：官方 `scripts\install.ps1` 的 `$PythonVersion`/`$NodeVersion` 是唯一 selector；`requires-python` 是兼容声明不是选择器；`current.txt` 是运行时唯一指针（launchers 不硬编码 patch 目录、不 wildcard 枚举）。详见 references「uv-managed Python portable updates」。
- **每次构建全新 runtime**：不读任何旧 Portable 或运行时 seed 环境变量；copy 系统 uv/Node/Git 当可用，否则下载；隔离 Python + fresh locked venv 在 build 下。uv pin（2026-08-09 起无单独 SHA256 校验：zip CRC + HTTPS + 3x retry）；`--no-install-project --link-mode copy` 同步依赖，防 editable build-root 映射进 venv。
- 目标仅 Windows x64（构建期拒绝其他架构）。
- 构建代码/技能/部署副本保持 aligned：行为变化时同步改 README.txt、模板、launchers、updater、build script、references。
- **Ordering contract: 技能变更要在构建/打包前写回并同步进 stage**（observed 2026-08-26）。构建 mid-build 把 `builder\data\hermes-home\skills\...` 复制进 stage；此后编辑不会进产物除非手动重同步。必须随包发布的技能变更序列：(1) patch canonical builder 副本，(2) cp 进 stage 对应路径，(3) 才归档；(4) 归档后验证 ZIP 内 SKILL.md md5 == canonical。**纯技能/文案变更无需整轮构建**（observed 2026-09-04，规则对齐 deepseek-harness-portable-builder 技能，单向 cross-reference——该技能正文暂无回链）：patch canonical → cp 两个文件进 stage → 只跑归档 + ZIP md5 验证即可（前提：stage 是最近一次构建后的干净树，未启动过、无运行态数据——见 §14 Sanitize release state）；整轮 `Hermes.ps1` 只在代码/上游/运行时变化时跑。
- 打包 Portable 内维护脚本唯一位置：`<portable-root>\scripts\`（Update-Portable.ps1 含 `Patch|PatchRemove|SyncDesktop|WriteDesktopStamp` stages；Repair-Portable.ps1 自包含无参修复；Verify-Portable.ps1 校验）。技能目录不携带 scripts/templates/assets。
- 测试任何能杀进程的维护脚本（Repair 无 `-KeepProcesses`、SyncDesktop、Update.exe）**必须** `-KeepProcesses` 或只读模式（`-ShowOfficialPythonVersion`）；2026-08-10 无参测试曾杀死活着的桌面应用。测试后验证 Hermes 进程存活。
- Prefer bounded, high-signal verification：测试/修复路径反复失败或代价失衡时，停止、清理现场、修根因、只重跑相关门禁。
- **Build-log hygiene**: 后台长构建重定向 stdout/stderr 到 `builder\logs\build-YYYYMMDD-HHMMSS.log`；日志是最终报告的证据（引用真实退出码/产物名），非交付物。wrapper 用 `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; & '<script>'"`（不用 `-File`）让 npm/vite UTF-8 图形输出过 PS 5.1 GBK 控制台管道不损坏（`E2 94 82` → `E2 94 3F`, 2026-08-10）。在 BASH 层重定向 stdout（`... > log 2>&1`）——**不要**用 runner .ps1 在 PowerShell 内 `*>` 重定向（2026-09-02）：PS 5.1 `*>` 写 Out-File 默认 UTF-16LE（bash grep/tail 需先 iconv），且原生子进程（python 探针、npm）写继承句柄绕过 PS 流重定向泄漏到调用方 stdout（notify 快照显示截断的 traceback 片段看似构建失败——实为 `subprocess._readerthread` GBK 解码噪音，无害）。单通道 UTF-8 日志把退出码与完整 traceback 留在同一可 grep 文件。
- 不主动创建/讨论 checksum sidecar（除非 release contract 或用户要求）；直接做 archive integrity 验证。
- **HARD RULE（recurred 2026-08-24, 2026-09-02）**: 从 git-bash 调 powershell，双引号 `-Command "...$var..."` 会被 bash 展开 `$var`（`$_`/`$null`/`$LASTEXITCODE` 等）成空——写 temp `.ps1` 用 `-File`，或整个 `-Command` 单引号包裹，或完全避免 `$`。反斜杠转义 `\$var` 也不可靠（2026-08-26 JSON→bash→PS 链仍失败）。完整细节见 references「Windows PowerShell build execution」的 bash ↔ PowerShell interop 节。
- 编辑 `Hermes.ps1` 后必须保持 **UTF-8 BOM**（EF BB BF）：文本编辑工具会剥 BOM，PS 5.1 无 BOM 按 ANSI/GBK 解码，中文注释的悬挂字节吞掉换行、下一行代码被并入注释静默不执行（2026-08-23 三次构建失败）。python 字节级读写保 BOM + `Get-Content -Raw` 行数复查。
- 所有 root/path 比较必须**目录边界**（`root + Path.DirectorySeparatorChar` 或 Equals），禁止裸 `StartsWith(root)`/`-like '<root>*'`——前缀相同会误匹配其他 install（`...-Portable-Beta\`）。Update.cs 与 PS 脚本内所有路径过滤（IsForeignServe/StopPortableProcesses/FindForeignDashboardPids/WaitForRootProcessesExit）都查这一条。

## Self-Improvement Contract (mandatory)

Whenever you make a mistake while working on this project and then find the correct approach, **update this skill immediately in the same session** — do not wait to be asked, do not defer to a later task. This skill is the project's institutional memory; every error-to-fix cycle that is not written back is a lesson the next run will re-pay.

Trigger: you hit an error, a wrong assumption, a failed command, a user correction, or a rework — AND you then found the approach that actually worked.

How to record:

1. **Where**: default to `references/hermes-agent-windows-portable.md`（按主题进对应 appendix，没有就新增 appendix）。只有该教训是「每次必须遵守的操作规则/门禁」时才在主 SKILL.md 加 1-3 行规则（可含指向 references/代码函数名的指针）。主文件不再收纯考古（日期→症状→根因→修复的已 FIXED bug 史）——那只进 references。
2. **What**: symptom (exact error/behavior), root cause, the proven correct approach, and a one-line verification note. Name real files/functions where useful — but NOT line numbers (they shift; the skill itself has drifted stale twice). 行文措辞遵守「术语约定」段（见上文）。
3. **Copies**: three materializations, all byte-identical — builder canonical (`builder\data\hermes-home\skills\software-development\hermes-agent-portable-builder\`), the active profile copy (本机当前部署为 `C:\Hermes-Agent-Portable\data\hermes-home\skills\...`，Hermes 实际加载；若存在多份部署，每份活动 profile 都要同步), and the mid-build stage copy (`stage\Hermes-Agent-Portable\data\hermes-home\skills\...`). Patch the builder copy, copy BOTH files (SKILL.md + `references\hermes-agent-windows-portable.md`) to the profile copy always and into the stage copy whenever a build/pack must ship, and verify `diff` reports them byte-identical before finishing.
4. **Scope**: record only lessons that would save time if the same mistake recurs. When unsure whether a lesson is worth recording, record it — a concise pitfall is cheap. When a pitfall's forensic narrative grows longer than its actionable core, condense to the rule and keep deep detail in `references/` only.
5. **Report**: in the final reply, state what was added and that all copies (builder / active profile / stage) are byte-identical.

## User Trigger Rules (user-controlled)

同步、构建、打包、推送全部由用户明确指令触发,不从状态陈述、提问、上游提交、seed 变更或历史请求推断。当有疑问时,问。

- **同步**: 用户说"同步"时执行 upstream sync(见 Upstream Sync 节)。构建前的同步是流程内必做步骤,但"同步"作为独立动作也只执行于用户要求时。
- **构建/打包**: 用户说"构建/重新构建/打包"时才启动完整构建 (`Hermes.ps1`)。
- **推送**: 用户**没说「推送」就不要推送到仓库**。只有用户明确说了「推送」(如完整指令「同步、构建、打包、推送」)才执行 git commit + push 到 GitHub 仓库。改完代码、构建完成、打包完成都不自动推送。

## Upstream Sync (mandatory every build)

Before every build, sync `upstream/` to exactly mirror `origin/main`:

```powershell
git -C <builder-root>\upstream fetch --depth 1 --no-tags origin main
git -C <builder-root>\upstream reset --hard origin/main
```

`upstream/` is a read-only 官方源码 — a **depth-1 SHALLOW mirror** (`.git` 1.2GB+ → ~69MB). `reset --hard` guarantees a byte-exact mirror; unrelated local modifications must not survive. KEEP the shallow flags on every sync: a plain `git fetch --prune origin` would fetch every branch at full depth and silently bloat the mirror again. (Re-conversion recipe if the mirror is ever accidentally deepened: fetch depth-1 → reset → remove+re-add origin → delete ALL local tags → reflog expire → gc --prune=now --aggressive.)

## When to Use

- Auditing an Electron app's installer, unpacked build, or portable target.
- Bundling a Python, Node, Rust, or native service behind Electron.
- Converting an installed application into a directory-based portable ZIP.
- Diagnosing why a package builds successfully but fails on a clean machine.
- Reviewing electron-builder hooks, `extraResources`, ASAR boundaries, or native modules.
- Determining where application state, Electron `userData`, logs, and backend state are written.
- Diagnosing `hermes dashboard` failures: "Desktop IPC bridge is unavailable." toast, wrong frontend served, missing/stale web UI (`hermes_cli/web_dist`) — see the Web Dashboard section below.

## Prerequisites and How to Run (research path)

Research-only work (no build): pin the repository branch and record the resolved commit; read the app README, scoped `AGENTS.md`, root `package.json`, app `package.json`, and packaging scripts; trace Electron main-process path resolution and the child-process spawn environment; distinguish build-time payload staging from first-launch bootstrap installation; report the minimal portability changes, exact build commands, and pitfalls without modifying the upstream repository. Use repository source as authority — README build commands alone do not prove what is bundled.

For implementation work, additionally build an unpacked directory first, populate runtime payloads, launch from multiple paths, and only then archive it.

## Quick Reference

A distribution is not genuinely portable unless all of these are controlled:

| Concern | Portable requirement |
|---|---|
| Electron files | Shipped in unpacked directory or archive |
| Backend executable/runtime | Bundled and relocatable |
| Backend code/dependencies | Bundled, not fetched on first launch |
| Application state | Written beneath a stable portable data root |
| Electron `userData` | Redirected before paths derived from it are computed |
| Native modules | Built/staged for exact target platform and architecture |
| Updates | Disabled or redirected away from read-only packaged resources |
| Verification | Tested without system runtime, PATH shims, or pre-existing user state |

Directory-based ZIP portability is usually safer than a single-file portable executable when the app carries a large runtime. Single-file targets commonly self-extract to a temporary directory, making `resourcesPath` unsuitable as a persistent data root.

## Procedure

### 1. Establish the packaging graph

Trace the scripts behind `build`, `pack`, `dist`, and platform-specific targets, including lifecycle hooks (`prebuild`/`postbuild`, electron-builder `beforeBuild`/`beforePack`/`afterPack`/`afterSign`, custom wrappers). Record which step builds renderer assets, bundles the main process, stages native dependencies, writes build metadata, and creates installers.

### 2. Prove what is actually shipped

Inspect `build.files`, `extraResources`, `asar` and `asarUnpack`, hooks that return `false` to skip dependency collection, payload-copy scripts, and first-launch bootstrap code. Do not infer backend bundling from product copy — determine whether the runtime is physically in the artifact or downloaded after launch.

### 3. Trace writable roots

Find every root used for domain state/configuration, Electron `userData`, caches and logs, backend source/install tree, virtual environments, updater metadata, and connection/profile state. Environment variables or command-line overrides must be applied before module-level constants or `app.getPath('userData')`-derived paths are initialized. Redirecting only the backend home is insufficient if Electron still writes state to AppData/Library/XDG.

### 4. Trace backend resolution as an ordered ladder

Document each candidate in precedence order and its validation probe (explicit source/runtime override → 开发源码 → managed install → PATH command → system interpreter → first-launch bootstrap). Existence is not validity: verify imports, version/help commands, architecture, and required modules before selecting a candidate.

### 5. Trace the spawn contract

Capture executable, argv, working directory, environment merge order, `PATH` and runtime search paths, hidden-window flags on Windows, readiness protocol, and shutdown/updater ownership. A portable patch should reuse the app's canonical backend command builder rather than introducing a second launch path.

- The portable patch set is 6 (see builder README "构建对官方源码的修改"): path redirect, python backend, zoom 100% + test, language seed, translucency OFF (marker-bounded `HERMES_PORTABLE_TRANSLUCENCY_BEGIN/END`; PatchRemove restores captured originals byte-identical). The delayed zoom-restore patch was RETIRED 2026-08-26 (upstream fixed the underlying bug; see references). apps/shared is outside the desktop content-hash scope, so the translucency patch ships via full builds only.

### 6. Choose the minimum viable portable shape

Prefer: 1) launcher-only prototype → 2) native portable mode (derive paths from the executable directory early in Electron startup) → 3) packaging integration (extraResources/post-pack staging) → 4) custom updater model (only if the packaged backend must remain mutable). Practical layout: `App-Portable/` with `App.exe`, `resources/`, `data/` (app-home, electron-user-data), and a Portable launcher. Keep runtime resources read-only and all mutable state beneath `data/`.

### 7. Make the backend relocatable

Ordinary Python virtual environments are not automatically relocatable. Audit `pyvenv.cfg`, generated console-script launchers, editable-install `.pth` files, absolute paths in metadata/configuration, and native wheels/DLL search paths. On Windows, treat a tool's `--relocatable` flag as a hypothesis, not proof — renaming the parent directory is the decisive test. The robust fallback is a relative-path wrapper: derive portable root from `%~dp0`; locate bundled CPython under it; prepend a bundled `sitecustomize.py` bootstrap to `PYTHONPATH` (it calls `site.addsitedir()` on the retained `site-packages` so `.pth` hooks and pywin32 DLL setup still run after relocation — adding site-packages to PYTHONPATH directly does NOT process `.pth`); invoke `python.exe -m package.module %*`; selected through the app's existing explicit backend-command override. Prefer this over generated console-script shims; avoid editable installs pointing back at 构建源码.

### 8. Deduplicate managed runtimes using the upstream provisioning contract

Order of authority: official installer selector (`scripts\install.ps1` `$PythonVersion`) → runtime manager resolution (`uv python find <X.Y> --managed-python` + version probe) → canonical managed-directory name from real install output → retain it, rebuild venv against it, update every launcher/repair script to the exact retained path → prove the removed directory unreferenced and rerun all checks. The managed directory name is NOT a version oracle (exact `cpython-3.11.15-...` vs minor-series junction `cpython-3.11-...` may point at the same runtime — one runtime, not duplicates); store the validated active exact name in `runtime\python\current.txt`; launchers read only that pointer. Never use `requires-python` to choose or veto the selector. During explicit update: back up Python dir + venv + pointer; build and smoke-test the replacement before cutover; on failure restore all three. See references「uv-managed Python portable updates」and「Windows managed-python dedup」.

### 9. Stage target-native dependencies; embed the icon in every PE

Host architecture is not necessarily target architecture; restage/validate native modules immediately before packing. Compile `Hermes.exe`/`Update.exe` with the official icon embedded at compile time (`apps/desktop/assets/icon.ico` via `/win32icon:<path>`, `Hermes.cs`/`Update.cs` compiled `/target:winexe`); fail the build if the icon is missing; inspect each PE for an icon group before archiving.

### 10. Build unpacked first, then archive with a collision-free name

Run all release gates against the exact staging tree — staging is the authoritative test surface (Python contract, import probes, 内嵌源码 cleanliness, launcher icons, timestamped name, README/entry-point contract, skill presence/content). Do NOT routinely extract the just-created ZIP and rerun the functional suite (compression does not transform payloads); post-archive verification = archive integrity + inventory checks for required/forbidden paths. Extract-and-rerun only when archive semantics changed, corruption is suspected, or the user asks. Name: `Hermes-Agent-Portable-<version>-win-x64-yyyyMMdd-HHmmss.zip` (builder local time; never overwrite an older artifact of the same Hermes version). Archive inventory: 任何 `__pycache__` 只能出现在 `runtime\python\...\Lib\`（stdlib 预编译）；`data\hermes-home\hermes-agent\` 下有即清理时机错误（源码树清理必须排在 web UI stamp 步骤之后，见 references「Prebuilt web/TUI bundles & build stamps」）。

### 11. Verify portability

Test from a short ASCII path, a path with spaces, a Unicode path, a deeper directory, and a clean user profile/sandbox. Confirm: backend starts and announces readiness; no system Python/runtime used; no download/bootstrap occurs; no state written outside the portable root; restart preserves state; native terminal/process functionality works; moving the whole directory does not break imports or launchers.

### 12. Design and test the updater as part of portability

A runtime wrapper that launches a moved application does not prove an upstream self-updater is portable. The robust Windows update chain has five explicit phases:

1. **Preflight/repair:** probe the retained venv by importing core modules (probe stderr/nonzero exit = unhealthy venv, not a terminating exception); recreate with bundled CPython + locked deps; preserve the old venv until imports pass.
2. **源码清洁准备:** remove only the marker-bounded generated Portable source patch before invoking upstream. 内嵌 Git 源码必须 retain every tracked file + `git status --porcelain` empty + `git stash list` empty (deleting tracked sources creates apparent deletions → updater autostash prompt). Repository-local `core.longpaths=true`, `core.autocrlf=false`, `core.eol=lf`. Repair a 损坏源码 with a short temporary `subst` path when necessary.
3. **Official source update:** invoke upstream updater from the repaired venv; require terminal success. Network failure ≠ runtime-repair failure. `Restore local changes now? [Y/n]` means preflight failed; fix the source, never automate the answer.
4. **Portable Desktop rebuild/sync:** reapply the bounded early portable-path patch to fresh Electron source, build `win-unpacked`, validate `Hermes.exe`, stage a complete `app.portable-next`, atomically swap with live `app` retaining rollback material. Patch apply/remove must be idempotent; remove succeeds when patch/Desktop source already absent. SyncDesktop self-cleans (runs PatchRemove after the swap) so the 内嵌源码 exits pristine; Update.exe's pre-update PatchRemove + `git clean -fd` stays as safety net. Three update-chain scripts merged into one `Update-Portable.ps1` 2026-08-10 (`Patch|PatchRemove|SyncDesktop|WriteDesktopStamp`; `Repair` split back out into standalone `Repair-Portable.ps1` same day).
5. **Cleanup/verification:** remove the generated patch again before archiving the next release; launch the replaced Desktop directly; confirm embedded backend listens on loopback; remove interruption markers and rollback trees; then remove only untracked/ignored build caches after the live app owns the artifact.

When a PS 5.1 helper edits UTF-8 source, read/write explicitly as UTF-8 (`[System.IO.File]::ReadAllText(..., [Text.Encoding]::UTF8)`, write UTF-8 without BOM); plain `Get-Content -Raw` can corrupt non-ASCII comments. Before rebuilding, check the patch changes only its bounded marker block (`git diff --numstat`, `git diff --check`). Do not ship an `Update.cmd` merely because `hermes update` exists — run it end to end (real upstream change or controlled broken-trampoline fixture), inspect the log to the terminal success marker, restart Desktop, verify new commit/version + backend readiness.

### 13. Customize first-run UI defaults without breaking user persistence

A requested "default" is a first-run seed, not a value forced on every launch. Inspect upstream default vs runtime/browser baseline (Electron zoom: Chromium actual size is 100%, app may ship another preset); report the distinction before committing when it could change the user's choice. Change the app-level default through a bounded, removable build patch + update the directly corresponding tests; preserve saved user values on later launches; verify a manual change survives restart. For zoom: convert percent `p` with `log(p/100)/log(1.2)`; reassert persisted value after the window's initialization settles (a correct `zoom-state.json` does not prove the active window kept the zoom). Test through the real Portable launcher — directly starting `app/App.exe` may bypass launcher overrides. See references「Electron first-run defaults」.

### 14. Sanitize release state before archiving

Never archive from a staging tree launched without resetting its writable data roots (smoke tests silently seed onboarding flags, sessions, credentials, memories, logs, caches, SQLite). For a fresh-user ZIP:

1. stop every process whose executable is beneath the staging root;
2. empty Electron `userData` completely (retain only the empty directory);
3. remove backend user state (`.env`, `auth.json`, `state.db*`, sessions, logs, memories, caches, snapshots, profiles, pairing state, user-created skills/plugins) unless the distribution intentionally seeds them. NO intentional seed files under `builder\data\hermes-home\memories\` as of 2026-08-13; the single `builder\data` → `data` Copy-Tree block ships anything added there — do not add `MEMORY.md`. The skill-overlay gate enumerates `builder\data` dynamically (strips leading `hermes-home/`, checks every seed against the embedded checkout's index) — renaming/adding/removing a seed never needs a script edit.
4. retain only required runtimes/source plus a minimal non-secret default config. Release ships WITHOUT `data\hermes-home\config.yaml` (stage assembled from an UNLAUNCHED upstream 源码; absence is the designed state, not a missing seed). zh default is a first-run launcher seed (`Hermes.cs::EnsureFirstRunConfig` `FileMode.CreateNew` + SyncDesktop `flag:'wx'`, both write `display:\n  language: zh\n` only when absent) — existing user config never overwritten. Do NOT add config.yaml to `builder\data`; do not change upstream `DEFAULT_LOCALE`.
5. audit the archive inventory for forbidden state after packaging;
6. validate onboarding from a disposable extraction, then discard it — never rearchive the launched fixture.

Do not infer a removed onboarding screen from a returning-user launch; inspect persisted first-run keys and test with empty `userData`; use Electron CDP (`--remote-debugging-port`) text assertions when screenshots are unavailable.

### 15. Clean verification artifacts immediately

Treat extracted-archive smoke-test directories as disposable fixtures, not deliverables. After final verification: stop processes under each test root; delete every extraction/relocation test directory in the same task; verify absence; retain only the release archive, source/build workspace, and any explicitly requested staging directory. Do not leave multiple same-named portable trees. Long-path deletion: temporary `subst` drive, or mirror empty dir with `robocopy /MIR` then remove. Routine builds should not create an extracted fixture at all.

## Build-time rules (active)

Rules that still require a human to follow (implementation lives in the named code; these are the operational edges). 每个都是复犯过的坑。

- 构建前杀干净 stage 进程（Electron、python、git 子进程——不只 `Hermes.exe`）：活进程既锁 DLL/venv 又可能被 kill 的 git fetch 留 `tmp_pack_*`/FETCH_HEAD 锁，锁下一轮 stage 删除。构建中 `.git\objects\pack\tmp_pack_*` 会使归档膨胀数百 MB —— Hermes.ps1 归档前删，手动打包也要删。
- 从 git-bash 手动调 `7za.exe`：归档/`-o` 目标路径用 cwd-relative 或 native `D:\...`；MSYS `/d/...` 绝对路径不被可靠转换（"Everything is Ok" 但文件写丢）。`7za l` 输出 grep 用 `grep -F` 固定串或 basename token——反斜杠路径正则经 JSON→bash 折叠成转义（2026-08-11）。
- patch 工具编辑含 Windows 反斜杠/`\r\n` 转义的文本会把字面 `\\`/真实 CRLF 写进文件（diff 显示正常，2026-08-12/26）：用 python 字节级 read/replace/write + count 断言，`od -c` 复查单反斜杠。
- 断网 boot 模拟：proxy 黑洞不足（git fetch 忽略 proxy env）；用 `netsh advfirewall firewall add rule ... action=block` 对 launcher、内层 `app\Hermes.exe`、bundled python.exe 建 OUTBOUND 规则。清理契约见 references（config.yaml 必删，保留 `git/ hermes-agent/ node/ skills/ SOUL.md web-ui-build-stamp.json`）。
- 原生探针（构建/repair 里 venv import 检查）必须显式 `$env:PYTHONPATH = @($checkout, $old) -join ';'` 并在用后恢复：`uv sync --no-install-project` 不装项目自身，靠 PYTHONPATH 解析；省略会吃构建机 ambient PYTHONPATH 的 false green / false fail。
- npm/electron cache（`builder\assets\npm-cache`/`electron-cache`/`electron-builder-cache`）content-addressed——不要 prune。workspace 依赖 hoisting 不保证：electron 只在 `apps\desktop\devDependencies`（root package.json 不再声明），TS2307/找不到包时查 `apps\desktop\node_modules\`，`$electronVersion` 读取 fallback root→desktop（Hermes.ps1 已实现）。
- 验证 exe 内中文字符串：整文件 `Unicode.GetString(...).Contains(中文)` 不可靠（命令行传输破坏 + PE 头误读，漏报实测）——用目标串 UTF-16LE hex 字节搜索。字符串缺失=必定旧版；含字符串≠行为正确。
- 长路径删除失败：`subst` 临时盘或 robocopy /MIR 空目录镜像后删除目标；deep venv/source 清理非致命处理。
- 更新/归档永不删 Git-tracked 源码来减体积——只 prune ignored build/cache trees（tracked 删除 = 数千 apparent deletions → stash prompt）。
- `Update-Portable.ps1` TUI/web 步骤后 lockfile restore 必须 `& git.exe -C $Repo checkout -- package-lock.json` 显式 `-C`（Push/Pop-Location 后 cwd 在 repo 外；bare `git checkout` 报 not-a-repository，且 SyncDesktop 的 catch 会误删刚建好的 bundle）。
- venv 必须 `uv venv --relocatable`；已部署旧副本 `Repair-Portable.ps1 -UpdatePython -KeepProcesses` 修复 + 重启 app（非 relocatable venv 的 `pyvenv.cfg home` 指向 builder build tree → 后端从 build\ 执行、锁 DLL 挡下一轮构建）。
- 维护脚本副本（builder\scripts ↔ deployed `<portable-root>\scripts` ↔ stage）必须 byte-identical——改模板时所有部署副本同改；如 `UV_NO_CONFIG=1` 类的 helper 级修复。
- Repair helper 不要设 `UV_NO_CONFIG=1`：它剥掉整个 `[tool.uv]`（override-dependencies / exclude-newer），uv 重解析、`--locked` 失败。用 `uv lock --check` + `--locked --dry-run` 在 exact helper env 验证。
- Update.cs 编译/测试守则：.NET 4.0 csc = C# 5（无 `out var`）；`/target:winexe`（exe 会弹黑窗）；子进程全部 `CreateNoWindow=true`；port 前先做 window+streaming spike 测试。csc 从 git-bash 调：路径正斜杠 + `-out:` 前缀选项。详见 references「Windows csc compilation」。
- runtime 输出编码修复层：Update.cs Main 前置 `Console.OutputEncoding = UTF8` + `PYTHONIOENCODING=utf-8`/`PYTHONUTF8=1`（finally 恢复）；spawn 的 PS 子进程用 `-Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; & '<script>'"`（单引号路径、`'` 转 `''`）——否则孙进程 UTF-8 输出被 GBK 链双重错码（`�?` 集中在 SyncDesktop 日志）。详见 references「Windows console output encoding」。

## Failure-mode pointers (debugging)

已 FIXED 失败模式的症状→处置速查；完整考古链在 references 对应 appendix。

| Symptom | Likely cause & action | Detail |
|---|---|---|
| overlay 更新后 `git log` 停旧 commit、status 数百 M | loose `refs/heads/main` 旧文件压过 packed-refs（gc 后 refs/heads/ 空）；处置：`git update-ref refs/heads/main <shipped-sha>`（构建已由 Convert-PackagedGitToShallow 自动写 loose ref） | references「Packaged .git conversion」 |
| Windows 上 `git status` 永久脏且 diff 空 | 上游大小写双路径（case-collision）或 EOL-only；处置：前者构建内 Protect-CaseCollisionEntries 已处理、勿改上游；后者 `git checkout -- <file>` + 确认 autocrlf | references「Packaged .git conversion」/「Desktop patch line endings」 |
| 端用户解包后 update 报 Local changes / stash prompt | ZIP 提取 mtime 变化触发 stat cache 失效（CRLF worktree vs LF index）；构建内 `git rm -r --cached` + `reset --hard` 已修复；怀疑包内 git 时用干净提取验证 | references「Packaged .git conversion」 |
| hermes update 失败 + venv 残留两个 cryptography dist-info / jiter pyd 拒绝访问 | DLL 锁（外锁或自锁）；Update.cs 已前置解锁 + site-packages 副本 | references「hermes update failure classes」 |
| hermes update exit 1 无输出 / git log 读不到 commit | shallow-boundary 损坏（.git/shallow 丢 HEAD 边界）；Update.cs 探测 `git log -1` + `fetch --depth 1` 重立边界；手动：停进程后 `git reset --hard origin/main` | references「hermes update failure classes」 |
| update "Code did not move (detached checkout)" | 上次失败更新留半应用 merge tree；Update.cs `reset --hard HEAD` + `checkout main` 前置修复 | references「hermes update failure classes」 |
| shallow 包上 `git merge --ff-only` 报 unrelated histories | 正常：官方 updater 的 depth-1 fetch 重嫁接边界，走 reset fallback；只认 "Code did not move" 真失败 | references「hermes update failure classes」 |
| npm 升级后 corepack MODULE_NOT_FOUND | `npm i --prefix <node> npm@x` prune 了 corepack；修复=升级前读 bundled 版本、后重装（Ensure-OfficialNpm 已实现；Verify gates: corepack.js + corepack.cmd） | references「Prebuilt web/TUI bundles & build stamps」 |
| SyncDesktop 每次都重建 desktop | desktop-build-stamp.json 哈希不匹配（stamp 须写在行尾规范化 `git rm --cached`+reset 之后）；web UI 首启跑 npm install = web stamp 缺失/CRLF 哈希 | references「Prebuilt web/TUI bundles & build stamps」 |
| 构建 "install 成功但 find 间歇失败" exit -1 无 stderr | `原生 | Select-Object -First 1` 管道竞态；子表达式先收全输出 `(& $Uv python find ...) | Select -First 1` | references「Windows PowerShell native commands」Pitfall 1 |
| PS 5.1 脚本因原生 stderr 行中止（npm notice/uv/git warning） | NativeCommandError 假象；Invoke-NativeChecked（EAP Continue + 只看 $LASTEXITCODE，勿 2>&1 合并） | references「Windows PowerShell native commands」Pitfall 2 |
| desktop 后端从 builder build\ 执行、DLL 锁挡重建 | 旧非 relocatable venv；Repair -UpdatePython + 重启 | references「uv-managed Python portable updates」 |
| Update.exe 更新器日志 SyncDesktop 段乱码 `�?` | 子 PS GBK OutputEncoding 双重错码；`-Command` UTF8 wrapper | references「Windows console output encoding」 |
| 7za x 到 <root> 后 root 空、文件在嵌套子目录 | top-folder 归档要解到 parent（`-o(Split-Path <root> -Parent)`）+ 解后 sanity check | references「Windows PowerShell native commands」Pitfall 4 |
| 构建 stage 被占用删不掉 | 有进程 cwd/句柄在 stage 树（含自启修复的 git fetch）；杀全部 stage 进程后重跑 | — |
| upstream fetch 失败但 api.github.com 通 | 本地 DNS 把 github.com 解析到不可路由 APAC edge；`curl --resolve` 探活 + hosts pin（edge IP 不稳，每次构建时重探）；或走代理 | references「Update.exe error dialog」§5 |

## Verification

Before reporting success, provide real output for:

1. target build command and exit status
2. packaged file inventory proving the runtime/backend exists
3. backend import or `--help` probe using the bundled runtime
4. application startup/readiness
5. filesystem audit showing writes remain under the portable root
6. move-and-relaunch test
7. updater dry check plus a real update/repair test from a moved root, with all app processes stopped
8. proof that immediately before upstream update the 内嵌源码 has empty `git status --porcelain` and empty `git stash list`, and that the generated Portable patch can apply/remove/apply without duplication
9. proof that the updater reached its terminal success state, cleared interruption markers, and left imports/backend readiness healthy
10. archive integrity and inventory checks after compression; extract and rerun only when archive/extraction behavior changed, corruption is suspected, or the user explicitly requests it
11. release-state audit proving Electron `userData` has no payload and backend home contains no state database, credentials, sessions, logs, memories, or caches beyond intentional distribution defaults
12. first-run/onboarding verification from a clean staging data root before compression, using screenshot/AX evidence or Electron CDP text assertions; use disposable extraction only when extraction behavior is the feature under test
13. when a checksum artifact is part of the requested release, regenerate it after the final sanitized rebuild and compare a read-back hash against the manifest; when the user explicitly requests no checksum file, verify archive integrity directly without creating or reporting an omitted checksum artifact

After any source edit made after packaging, rebuild the affected artifact, replace it in staging, rerun staging gates, and regenerate the archive. Do not repeat a full extraction smoke test unless the archive/extraction layer is itself under test.

On cross-platform suites, separate product regressions from host-incompatible fixtures. If a full Windows run fails only tests that fabricate POSIX absolute paths, record the aggregate result, then run the directly relevant Windows/portable test files plus typecheck, lint, and runtime smoke tests. Do not describe the full suite as passing.

For research-only requests, clearly separate proven upstream behavior from proposed modifications and name the exact source files supporting each conclusion.

## Web Dashboard: two frontends, one server

Use when `hermes dashboard` serves the wrong frontend, a browser or the desktop preview pane shows **"Desktop IPC bridge is unavailable."**, or the web UI is missing/stale (`hermes_cli/web_dist`). Two different bundles share the same FastAPI dashboard server:

| Bundle | Location | Browser behavior |
|---|---|---|
| Desktop bundle | `app.asar.unpacked/dist` (has `electron-main.mjs`, `electron-preload.js`) | Requires `window.hermesDesktop` (Electron preload IPC bridge). Plain browser/preview pane has no bridge → boot hook fires the toast and disables chat. |
| Web bundle | built from `web/` (vite, `outDir: ../hermes_cli/web_dist`) | Browser-native. Chat drives the agent over `/api/ws` + `/api/pty`. Server injects `__HERMES_SESSION_TOKEN__` / `__HERMES_BASE_PATH__` / `__HERMES_AUTH_REQUIRED__`. |

Resolution rule (`hermes_cli/web_server.py`): `WEB_DIST = $HERMES_WEB_DIST` if set, else `<hermes_cli>/web_dist`. **The env var wins.**

Root cause of the leak: the desktop app spawns its backend with `HERMES_WEB_DIST=<app.asar.unpacked/dist>` + `HERMES_DESKTOP=1`, and every agent/user terminal spawned from the app inherits it — a standalone `hermes dashboard` from such a shell serves the DESKTOP bundle. The desktop-embedded flow is correct and must keep the desktop bundle (it spawns `python -m hermes_cli.main` directly, never through `hermes-cli.cmd`).

### Diagnose

```bash
curl -s http://127.0.0.1:9119/ | grep -oE '__HERMES_SESSION_TOKEN__|hermesDesktop|index-[A-Za-z0-9_]+\.js' | sort -u
```

- Web bundle: token present, bundle hash matches the web build (e.g. `index-Cv1Lntuh.js`).
- Desktop bundle: no token, served JS references `window.hermesDesktop`.

### Fix

1. Build the web frontend (network needed for first install): `cd <repo> && npm install --workspace web --include=dev --silent --no-fund --no-audit --progress=false && cd web && npm run build`. Output lands in `hermes_cli/web_dist` (vite outDir), the server's default fallback.
2. Launch with the override so a leaked inherited value can't win: `export HERMES_WEB_DIST="<repo>/hermes_cli/web_dist"` then `hermes-cli.cmd dashboard --no-open` (unsetting the var achieves the same).
3. Re-run the curl check: token + NEW bundle hash. Startup log should show `HERMES_DASHBOARD_READY port=9119`.

Durable fix in this project: `hermes-cli.cmd` clears `HERMES_WEB_DIST`; the build/update/verify scripts prebuild + assert `hermes_cli/web_dist` (fatal in build, non-fatal with stale-bundle deletion in update, mirroring the TUI contract); staged `git clean -fdx` excludes `hermes_cli/web_dist/`.

### Pitfalls

- `hermes_cli/web_dist/` is gitignored upstream — `git clean -fdx` wipes it unless excluded with `-e` (mirror `-e hermes_cli/tui_dist/`).
- `web` is a root npm workspace; its lockfile is the ROOT `package-lock.json`. `npm install --workspace web` can rewrite it → restore with `git checkout -- package-lock.json` (same guard as the TUI step).
- The desktop preview pane is a plain webview WITHOUT the bridge — desktop-bundle pages are expected to fail there; that's a symptom, not a bug.
- No runtime frontend build: without `web_dist` the server returns the JSON "Frontend not built" error. The lazy install only covers fastapi/uvicorn extras.
- The dashboard backend also ticks cron and gateway pub/sub — a real backend, not a static file server; don't kill it casually.
- After fixing, restart the dashboard process (it caches `WEB_DIST` at import time).

## References

- `references/hermes-agent-windows-portable.md` — SINGLE consolidated reference file (merged 2026-08-10 from the former 12-file layout). Main body: upstream packaging + portable conversion source audit notes. Appendices (read by their `# Appendix:` heading):
  - `Desktop patch line endings` — CRLF/EOL-only false positive causal chain
  - `Electron first-run defaults` — zoom/onboarding verification recipe
  - `Portable builder web-dist fix` — web dashboard durable fix + sync contract
  - `Portable install diagnosis` — "Desktop IPC bridge is unavailable." root cause
  - `uv-managed Python portable updates` — update transaction design
  - `Windows managed-python dedup` — interpreter dedup decision procedure
  - `Windows csc compilation` — csc 4.0/C#5 + Updater EXE patterns
  - `Windows console output encoding` — UTF-8 decode ladder, tofu, WaitForExit, select race
  - `Windows PowerShell build execution` — exit-code masking, bash↔PS interop, cmd pitfalls, stage-tree checks, official repo updater design
  - `Windows PowerShell native commands` — 6 pitfalls (select race, NativeCommandError, env leaks, MSYS paths, grep backslashes, flaky downloads)
  - `Update.exe error dialog` — Update.cs failure dialog + network-outage/git-dubious-ownership ladders
  - `Packaged .git conversion` — shallow-ization sequence, loose-ref, case-collision, CRLF stat-cache, tmp_pack, stage-delete timing (2026-09-02 consolidated from SKILL.md)
  - `hermes update failure classes` — DLL-lock (external + self-lock), detached-checkout, shallow-boundary corruption, ff-only expectation, Update.cs protections (2026-09-02 consolidated from SKILL.md)
  - `Prebuilt web/TUI bundles & build stamps` — stamp ordering, npm hoisting/corepack, package-lock restore, offline caches (2026-09-02 consolidated from SKILL.md)
- MERGED 2026-08-10: the two pre-archive release gates became in-script functions `Test-PortablePythonContract` / `Test-PortableNoEditableInstall` inside `Hermes.ps1`. VERIFY after such a merge: PowerShell AST extraction of a function body (`FunctionDefinitionAst.Body.Extent.Text`) ALREADY includes the outer `{ }`, so rebuilding a function as `"function Name([string]$Root) { <body> }"` nests a scriptblock literal and the function silently returns its own source text as a string. Correct reconstruction is `"function Name([string]$Root) <body>"` with NO extra braces.
