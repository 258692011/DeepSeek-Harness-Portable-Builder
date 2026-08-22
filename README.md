# DeepSeek Harness Portable Builder

此目录是本机 Portable 构建系统，不是官方 Git 仓库的一部分；官方源码只存放在 `upstream\` 子目录中。

本构建器将 [DeepSeek Harness (dsh)](https://github.com/deepseek-ai/deepseek-harness) 打包成免安装、可移动的 Windows x64 便携版：内嵌 Node.js 运行时 + 预构建的 dsh 应用，用户数据通过 `DSH_HOME` 重定向到包内 `data\dsh\`，整包可复制、移动。

## 目录职责

```
DeepSeek-Harness-Portable-Builder\
├── README.md                        # 本说明文档（构建器用法）
├── builder\                         # 本地构建器实现；只在构建机使用
│   ├── source\                      # 构建脚本（入口 DeepSeek-Harness.ps1）、启动器/更新器 C# 源码、README 模板、图标、pnpm 安装上下文
│   ├── assets\                      # 离线缓存（7zip\7za.exe、node\node-v22.23.2-win-x64.zip、git\PortableGit、pnpm），缺失时联网下载并回填缓存
│   ├── data\                        # 随包预置内容，构建时复制进成品 data\（skills、profile 补丁）
│   └── logs\                        # 构建日志（UTF-16LE，读取需 iconv -f UTF-16LE -t UTF-8）
├── upstream\                        # 只读镜像：deepseek-ai 官方 dsh 源码；可同步/重置到 origin/master
├── stage\                           # 组装后的未压缩 Portable 目录（DeepSeek-Harness-Portable\）
└── dist\                            # 最终 ZIP（DeepSeek-Harness-Portable-<ver>-win-x64-<时间戳>.zip）
```

## 构建机要求

当前只支持 Windows 10/11 x64。构建机需要：

| 软件 | 版本要求 | 是否必须预装 | 说明 |
|---|---|---:|---|
| Windows PowerShell | 5.1（Windows 自带） | 是 | 构建入口运行环境 |
| Git（PortableGit） | 固定 2.55.0.3 | 否 | 构建器**不读取系统**；从 `builder\assets\git\` 缓存取（解压后的目录），缺失才下载、解压并回填缓存；仅用于 `Assert-Upstream` 读取 `upstream\` 状态与提交号，不进成品 |
| pnpm | 固定 11.21.0 | 否 | 安装 `@deepseek-ai/dsh` 发布包；不用系统 pnpm，用包内 npm 装固定版本并回填 `builder\assets\pnpm\` 缓存（下载后回填 assets 缓存） |
| Node.js + npm | 固定 v22.23.2 | 否 | 打包进成品的 Node 运行时不读取系统，从 `builder\assets\node\` 缓存取，缺失才下载（下载后回填 assets 缓存） |
| .NET Framework C# 编译器 | v4.0（Windows 10/11 自带） | 是 | 使用 `Framework64\v4.0.30319\csc.exe` 编译 `DeepSeek Harness.exe` 启动器 |
| 7za.exe（7-Zip 命令行版） | 固定 26.02 | 否 | 使用 builder\assets\7zip\7za.exe 压缩，缺失时联网下载恢复（下载后回填 assets 缓存） |

版本规则：Node 固定 v22.23.2（满足 dsh `engines.node` `^22.19.0 || >=24.0.0`）；pnpm 固定 11.21.0（脚本 `$PnpmVersion`，v11 才支持 `--config.dangerously-allow-all-builds`）；Git 固定 2.55.0.3（脚本 `$gitTag`/`$gitVer`，自带 PortableGit 离线缓存，不再依赖系统 git）；dsh 版本跟随 `upstream\package.json` 的 `version` 字段（当前 `0.1.1-rc.2`，RC 阶段迭代频繁）。

## 构建

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  D:\DeepSeek-Harness-Portable-Builder\builder\source\DeepSeek-Harness.ps1
```

或带日志输出（推荐）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; & 'D:\DeepSeek-Harness-Portable-Builder\builder\source\DeepSeek-Harness.ps1' *>&1 | Tee-Object -FilePath 'D:\DeepSeek-Harness-Portable-Builder\builder\logs\build-<时间戳>.log'"
```

构建流程（约 2-4 分钟）：

1. 校验 `upstream\` 存在且为官方仓库
2. 清空并重建 `stage\DeepSeek-Harness-Portable\`
3. 解析便携 Node（own assets → 缺失则下载并回填缓存）
4. `pnpm add @deepseek-ai/dsh@<版本>`：`--config.node-linker=hoisted`（实体化 node_modules，无 symlink）+ `--registry=https://registry.npmjs.org/`（规避镜像源的 UND_ERR_DESTROYED）+ `--config.dangerously-allow-all-builds`（pnpm 11 默认拦截构建脚本会 exit 1）+ `--fetch-retries=5 --network-concurrency=8`（代理不稳时重试/降并发）
5. 编译 `DeepSeek Harness.exe`（csc winexe + DeepSeek 图标）
6. 注入 README 版本号 → `dsh --version` 验证 → web probe（启动 dsh web，HTTP 200 才算过）
7. 清理 probe 生成的 `data\dsh\`（junction 树不得进包）→ 7za 归档 → `7za t` 完整性验证

## 同步 upstream

构建前建议先同步（upstream 是只读镜像，可随时重置）：

```powershell
git -C "D:/DeepSeek-Harness-Portable-Builder/upstream" fetch --prune origin
git -C "D:/DeepSeek-Harness-Portable-Builder/upstream" reset --hard origin/master
```

## 便携包说明

产物结构：

```
DeepSeek-Harness-Portable\
├── DeepSeek Harness.exe   # 无窗口启动器：设 DSH_HOME → 启 dsh web → 开浏览器 → 托盘驻留
├── Update.exe             # 更新器（窗口界面）：包内 pnpm 快速升级 dsh，数据不动（见"升级策略"）
├── node\                  # Node v22.23.2 便携运行时
├── app\                   # @deepseek-ai/dsh（hoisted 实体化 node_modules，可归档迁移）
├── data\dsh\              # DSH_HOME 用户数据（profiles/storages，首次运行生成）
└── README.txt             # 给最终用户的说明
```

- 端口：固定 `http://127.0.0.1:3080`（dsh 惯例端口）；程序已在运行时再次双击只会打开该地址，不会启动第二个实例（无随机端口回退）
- 自愈：启动器每次启动删除 `data\dsh\profiles\node_modules`（symlink farm），dsh 启动时自动重建——便携包移动后无需任何手动修复
- 数据随包走：`data\dsh\` 内所有用户数据跟随目录移动

## 冒烟测试

解压到临时目录后按发布验证清单执行（完整细节见技能 `deepseek-harness-portable-builder`）：

1. CLI 版本：`node\node.exe app\node_modules\@deepseek-ai\dsh\lib\bin.js --version` → 输出版本号
2. 运行 `DeepSeek Harness.exe`，轮询 `http://127.0.0.1:3080/` → HTTP 200，页面标题 `<title>DeepSeek Harness</title>`
3. 自愈检查：`data\dsh\profiles\node_modules\@deepseek-ai` → 约 195 个 junction 条目（启动后自动重建）
4. 图标：可从 DeepSeek Harness.exe 提取 32x32 图标
5. 归档校验：`7za t` → "Everything is Ok"；`data\dsh` 只含预置内容（`profiles\web\cordis.patch.yml` + 官方脚手架、`skills\`），无探针生成的 junction farm 与 `storages`
6. Update.exe 为窗口版：其 UTF-16 字符串含 检查更新 / 立即更新 / 发现新版本（旧版 MessageBox 流程已移除）
7. 启动 Update.exe（不带参数）约 4 秒后进程仍存活（窗口构建无崩溃），随后 taskkill /F；残留的 `.dsh-update-in-progress` 标记带 PID 校验，下次运行自动忽略
8. 无头端到端（在便携包的副本上做，别动正式包）：复制 `app\` 到临时目录 → 删除 `node_modules\.modules.yaml` → 在副本内执行 `node\node.exe node\node_modules\pnpm\bin\pnpm.cjs add @deepseek-ai/dsh@latest --registry=https://registry.npmjs.org/ --config.node-linker=hoisted --config.dangerously-allow-all-builds --fetch-retries=5 --network-concurrency=8` → 退出码 0，package.json 依赖与 `bin.js --version` 均为 registry 最新版（如 0.1.1-rc.2），data\dsh 不受影响
9. `Update.exe --check`（托盘路径）打开窗口并自动检查一次——GUI 操作，每个版本人工验证一次

## 升级策略

dsh 是 RC 阶段、破坏性变更频繁。升级分两种情况：

① 原地更新 dsh(推荐,不需重建)
双击包根目录的 `Update.exe` 打开更新窗口:
- 窗口显示 当前版本 / 最新版本 / 状态;点击"检查更新"查询 registry(启动时不做任何自动检查)
- 更新前先用包内 node 探测 registry.npmjs.org 连通性(6 秒超时),网络/代理不通秒级报原因,不用干等 pnpm 重试;失败输出自动分类(网络/DNS/权限)
- 发现新版本后点击"立即更新":包内 pnpm(`pnpm add @deepseek-ai/dsh@latest`,带重试/降并发参数,代理不稳也能扛;`--config.minimum-release-age=0` 关闭 pnpm 的"新包需满 1 天"策略,否则刚发布的版本会被静默解析回旧版)→ 窗口内实时日志(无进度条,日志即进度)→ 弹框确认是否立即重启(是=重启并自动打开网页)(pnpm 打印 `Done in` 后进程可能赖着不退,更新器检测到完成标记后若进程未退出立即杀进程树(实测 Done 后通常 0.1s 自然退出;无需硬超时,失败由退出码/版本校验兜底))
- 只动 `app\node_modules`(含 pnpm-lock.yaml),用户数据 `data\dsh\` 原样保留
- 点击"立即更新"自动停止本目录运行的 DeepSeek Harness.exe / node 进程(杀进程树,托盘图标随之消失),其他目录的实例不触碰;无需手动退出
- 失败在窗口内显示(日志: `data\dsh\logs\Update-exe-diagnostic.log`)
- 托盘"检查更新":DeepSeek Harness.exe 托盘菜单打开同一窗口并自动检查一次(只查版本,不必退出程序)
- 注意:此方式不换 Node 运行时,也不更新启动器/图标

② 重新构建(当更新超出 dsh 本身时)
当新版 dsh 提升 Node engines 要求、或需要换启动器/图标/README 时:同步 upstream → 重新构建 → 产出新 zip(`dist\` 按时间戳区分,旧归档可自行清理)。

## 相关

- 便携版运行机制细节、坑点清单见 Hermes skill：`deepseek-harness-portable-builder`
