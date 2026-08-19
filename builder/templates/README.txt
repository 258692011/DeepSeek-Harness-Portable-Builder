DeepSeek-Harness-Portable — DeepSeek Harness 便携版
========================================

DeepSeek Harness (dsh) v{{DEEPSEEK_HARNESS_VERSION}} — 免安装、可移动的便携包。

系统要求
--------
- Windows 10/11 x64
- 无需安装 Node.js / Python / Git(全部内置于包内)

快速开始
--------
双击 DeepSeek-Harness.exe 启动 Web UI,浏览器自动打开 http://127.0.0.1:3080
(若 3080 被占用,启动器会自动改用空闲端口)

命令行方式:
    node\node.exe app\node_modules\@deepseek-ai\dsh\lib\bin.js --help

数据目录
--------
用户数据(profiles / storages / 配置)保存在包内 data\dsh\ 下,
整包可复制、移动,数据随身走。

首次启动
--------
首次运行会自动初始化 profile(约几秒),无需网络。
若包被移动后启动异常,删除 data\dsh\profiles\node_modules 再启动即可自愈。

组件版本
--------
- dsh: {{DEEPSEEK_HARNESS_VERSION}}
- Node.js: {{NODE_VERSION}}
- 源码提交: {{SOURCE_COMMIT}}

更新
----
双击包根目录的 Update.exe 即可原地升级 dsh(无窗口,使用包内 npm,
无需网络以外的工具;用户数据 data\dsh\ 原样保留)。
更新前请先退出 DeepSeek-Harness.exe(托盘图标 -> 退出)。
若 dsh 发布新版后提升了 Node 版本要求,或需要新图标/启动器改动,
则需重新构建便携包。
