Dodo — portable Windows notes and to-dos / Windows 桌面笔记与待办

Start / 启动
Extract the whole ZIP to a writable folder, then run Dodo.exe.
请将整个 ZIP 解压到可写目录，再运行 Dodo.exe。
The recommended portable ZIP includes .NET. The no-runtime ZIP requires
Microsoft .NET 10 Desktop Runtime x64. Both packages require Windows x64.
推荐 portable ZIP 已包含 .NET；no-runtime ZIP 需要安装 .NET 10 桌面运行时 x64。

Your data / 数据
Your profile is stored beside Dodo.exe in dodo-data/. Keep that directory when
updating application files. Use Settings → General → Backup and restore before
replacing data. A restore replaces the profile; it does not merge profiles.
数据保存在 Dodo.exe 旁的 dodo-data/。更新程序时保留该目录。
建议先用“设置 → 常规 → 备份与恢复”备份；恢复会替换当前数据，不会合并。
Markdown export writes a .md file and, when needed, an accompanying asset folder.
Move the file and its image folder together. It is an export, not two-way sync.
Markdown 导出包含 .md 和必要的图片目录；移动时应一起移动，不提供双向同步。

Plugins / 插件
The package includes the native Focus Timer and Sample Clock plugins.
These plugins, notes and to-dos do not need WebView2. Web plugins are not bundled;
if you install one separately, it requires Microsoft's WebView2 Evergreen Runtime.
内置原生“专注计时器”和“原生时钟”。笔记、待办及这两个插件不需要 WebView2。
Web 插件未随包提供；另行安装 Web 插件时，需要 Microsoft WebView2 Evergreen Runtime。

Licenses / 许可
Dodo is derived from PaperTodo. Read LICENSE.md, REQUIRED-NOTICE.md,
THIRD-PARTY-NOTICES.md and licenses/ for the original and dependency terms.
Do not remove those notices when redistributing the package.
Dodo 基于 PaperTodo；完整许可及必要声明见上述文件，重新分发时必须保留。

Downloads and feedback / 下载与反馈
https://github.com/seiya058904/dodo/releases
https://github.com/seiya058904/dodo/issues
Original project / 原项目: https://github.com/snownico0722/PaperTodo
