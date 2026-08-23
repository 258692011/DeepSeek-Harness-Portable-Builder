DeepSeek-Harness-Portable — DeepSeek Harness 便携版
========================================

DeepSeek Harness (dsh) v{{DEEPSEEK_HARNESS_VERSION}} — 免安装、可移动的便携包。

系统要求
--------
- Windows 10/11 x64
- 无需安装 Node.js / Python / Git(全部内置于包内)
- WebView2 运行时(Windows 10/11 大多自带;缺失时应用会提示并引导安装)

快速开始
--------
双击 DeepSeek Harness.exe 启动,应用窗口内打开 Web UI http://127.0.0.1:3080
(程序已在运行时再次双击,会唤起已有的应用窗口并置前,不会启动第二个实例;
 点窗口右上角关闭只隐藏到托盘,托盘图标 -> 退出才真正退出;
 应用窗口默认 1200x800,调整大小后自动记忆(data\webview2\window-state.ini);
 托盘菜单"打开网页"可在系统默认浏览器中打开同一界面)

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
双击包根目录的 Update.exe 打开更新窗口,点击“检查更新”查看最新版本,再点“立即更新”即可原地升级 dsh(使用包内 pnpm 快速安装,
无需网络以外的工具;用户数据 data\dsh\ 原样保留)。
更新时会自动停止本便携包正在运行的 DeepSeek Harness 进程,完成后按提示重启即可。
若 dsh 发布新版后提升了 Node 版本要求,或需要新图标/启动器改动,
则需重新构建便携包。
