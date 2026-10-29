# Dodo 离屏视觉检查

使用产品中的真实 `PaperWindow`、`EdgeCapsuleHost` 内容与设置控件，通过 WPF `RenderTargetBitmap` 输出 2× PNG。不会调用 `Show` / `Activate`、创建原生 HWND、发送鼠标键盘输入或使用剪贴板；不启动主应用、托盘或 MCP。

```powershell
dotnet run --project tools/Dodo.VisualReview -c Release -- zh-CN light .local/visual-review
dotnet run --project tools/Dodo.VisualReview -c Release -- zh-CN dark .local/visual-review
dotnet run --project tools/Dodo.VisualReview -c Release -- en-US light .local/visual-review
dotnet run --project tools/Dodo.VisualReview -c Release -- en-US dark .local/visual-review
```

每次生成待办、笔记、普通胶囊、窄长标题计数、设置五张图片和一份证据说明。语言在应用中按进程初始化，因此四种组合需顺序运行。工具只在自己的构建输出目录下写入 `dodo-data/data.json` 测试配置；不要将工具复制到正式应用目录运行。

渲染后复用同一批控件运行 12 项后台兼容性断言：未提交中英文 Note/Todo 搜索文本、全局匹配、未构造窗口的模型回退、插件正文排除，以及数量设置的 API、区域重建与保存失败回滚。不可见设置窗口按产品设计不会调度区域刷新，因此这里直接运行已注册的区域构建器；不执行真实窗口恢复默认或搜索结果跳转。

`narrow-count` 是实际贴边胶囊内容 Grid 的布局裁切，不是完整贴边窗口截图。其余图片使用真实窗口内容树。它们适合视觉 QA 和标为离屏界面渲染的文档配图，不能代替真实桌面的多屏 DPI、原生材质、拖拽、悬停、输入命中或窗口交接验收。
