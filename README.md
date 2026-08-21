# DeepSeek Harness Portable Builder

此目录是本机 Portable 构建系统，不是官方 Git 仓库的一部分；官方源码只存放在 `upstream\` 子目录中。

本构建器将 [DeepSeek Harness (dsh)](https://github.com/deepseek-ai/deepseek-harness) 打包成免安装、可移动的 Windows x64 便携版：内嵌 Node.js 运行时 + 预构建的 dsh 应用，用户数据通过 `DSH_HOME` 重定向到包内 `data\dsh\`，整包可复制、移动。

## 目录职责

```
DeepSeek-Harness-Portable-Builder\
├── README.md                        # 本说明文档（构建器用法）
├── builder\                         # 本地构建器实现；只在构建机使用
│   ├── source\                      # 构建脚本（入口 DeepSeek-Harness.ps1）、启动器/更新器 C# 源码、README 模板、图标、pnpm 安装上下文
│   ├── assets\                      # 离线缓存（7zip\7za.exe、node\node-v22.23.2-win-x64.zip），缺失时联网下载并回填缓存
│   ├── data\                        # 随包预置内容，构建时复制进成品 data\（skills、profile 补丁）
│   └── logs\                        # 构建日志（UTF-16LE，读取需 iconv -f UTF-16LE -t UTF-8）
├── upstream\                        # 只读镜像：deepseek-ai 官方 dsh 源码；可同步/重置到 origin/master（默认分支 master，不是 main）
├── stage\                           # 组装后的未压缩 Portable 目录（DeepSeek-Harness-Portable\）
└── dist\                            # 最终 ZIP（DeepSeek-Harness-Portable-<ver>-win-x64-<时间戳>.zip）
```

## 构建机要求

当前只支持 Windows 10/11 x64。构建机需要：

| 软件 | 版本要求 | 是否必须预装 | 说明 |
|---|---|---:|---|
| Windows PowerShell | 5.1（Windows 自带） | 是 | 构建入口运行环境 |
| Git | 任意版本 | 是 | 构建脚本 `Assert-Upstream` 读取 `upstream\` 状态与提交号 |
| pnpm | 固定 11.21.0（脚本 `$PnpmVersion`） | 否 | 安装 `@deepseek-ai/dsh` 发布包；不用系统 pnpm，用包内 npm 装固定版本并回填 `builder\assets\pnpm\` 缓存（全离线自包含） |
| Node.js + npm | 构建机任意可用版本即可 | 否 | 打包进成品的 Node 运行时不读取系统，从 `builder\assets\node\` 缓存取，缺失才下载（下载后回填 assets 缓存）；包内 npm 仅用于装固定版 pnpm |
| .NET Framework C# 编译器 | v4.0（Windows 10/11 自带） | 是（系统组件） | 使用 `Framework64\v4.0.30319\csc.exe` 编译 `DeepSeek Harness.exe` 启动器 |
| 7za.exe（7-Zip 命令行版） | 随仓库内置 `builder\assets\7zip\7za.exe`（当前 26.02） | 否 | 缺失时联网下载恢复（下载后回填 assets 缓存） |

版本规则：Node 固定 v22.23.2（满足 dsh `engines.node` `^22.19.0 || >=24.0.0`）；pnpm 固定 11.21.0（脚本 `$PnpmVersion`，v11 才支持 `--config.dangerously-allow-all-builds`）；dsh 版本跟随 `upstream\package.json` 的 `version` 字段（当前 `0.1.1-rc.1`，RC 阶段迭代频繁）。

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
4. `pnpm add @deepseek-ai/dsh@<版本>`：`--config.node-linker=hoisted`（实体化 node_modules，无 symlink）+ `--registry=https://registry.npmjs.org/`（规避镜像源的 UND_ERR_DESTROYED）+ `--config.dangerously-allow-all-builds`（pnpm 11 默认拦截构建脚本会 exit 1）
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
├── Update.exe             # 无窗口原地更新器：包内 npm 升级 dsh，数据不动（见"升级策略"）
├── node\                  # Node v22.23.2 便携运行时
├── app\                   # @deepseek-ai/dsh（hoisted 实体化 node_modules，可归档迁移）
├── data\dsh\              # DSH_HOME 用户数据（profiles/storages，首次运行生成）
└── README.txt             # 给最终用户的说明
```

- 端口：固定 `http://127.0.0.1:3080`（dsh 惯例端口）；程序已在运行时再次双击只会打开该地址，不会启动第二个实例（无随机端口回退）
- 自愈：启动器每次启动删除 `data\dsh\profiles\node_modules`（symlink farm），dsh 启动时自动重建——便携包移动后无需任何手动修复
- 数据随包走：`data\dsh\` 内所有用户数据跟随目录移动

## 冒烟测试

```powershell
# 1) 提取归档
# 2) 运行 CLI 验证
& "$env:LOCALAPPDATA\Temp\<解压目录>\DeepSeek-Harness-Portable\node\node.exe" `
  "app\node_modules\@deepseek-ai\dsh\lib\bin.js" --version
# 3) 启动 DeepSeek Harness.exe,探测 3080 端口应返回 HTTP 200
# 4) 归档内 data\dsh 应只有空目录(无 profiles/storages)
```

## 升级策略

dsh 是 RC 阶段、破坏性变更频繁。升级分两种情况：

① 原地更新 dsh(推荐,不需重建)
双击包根目录的 `Update.exe`(无窗口,用包内自带 npm):
- 读 `app\package.json` 当前版本 → 查 registry 最新版 → 确认 → 进度窗(显示版本 + 滚动条)→ `npm install @deepseek-ai/dsh@latest`(5 秒级)→ 自动重启 DeepSeek Harness.exe
- 只动 `app\node_modules`,用户数据 `data\dsh\` 原样保留
- 更新前检测 DeepSeek Harness.exe / 本包 node 进程,运行中会要求先退出
- 失败时弹带 复制日志/关闭 按钮的对话框(日志: `data\dsh\logs\Update-exe-diagnostic.log`)
- 托盘"检查更新":DeepSeek Harness.exe 托盘菜单可直接触发检查(只查版本,不必退出程序)
- 注意:此方式不换 Node 运行时,也不更新启动器/图标

② 重新构建(当更新超出 dsh 本身时)
当新版 dsh 提升 Node engines 要求、或需要换启动器/图标/README 时:同步 upstream → 重新构建 → 产出新 zip(`dist\` 按时间戳区分,旧归档可自行清理)。

## 相关

- 便携版运行机制细节、坑点清单见 Hermes skill：`deepseek-harness-portable-builder`
