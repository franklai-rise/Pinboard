# Pinboard

Pinboard 是一个本地优先的 Windows 视觉剪贴板工作台。它使用 PixPin 完成截图，使用 Excalidraw 提供无限平移、缩放、文字、直线、箭头和自由笔体验。画板可直接在程序内创建，并在左侧按项目分组、展开和切换；每张画板的场景、图片与 OCR 索引完整保存在单个 `.pinboard` 文件中。

界面默认使用英文。右上角的“中文 / EN”按钮可即时切换中英文，并会记住上一次选择；Excalidraw 画布工具栏会同步切换语言。

## 默认数据位置

- 项目与画板：`%USERPROFILE%\Documents\Pinboard\项目名\画板名.pinboard`
- 自动月度接收画板：`%USERPROFILE%\Documents\Pinboard\YYYY-MM.pinboard`
- 月度文字剪藏画板：`%USERPROFILE%\Documents\Pinboard\Text Clips\YYYY-MM.pinboard`
- 旧版文字历史（如已存在）：`%USERPROFILE%\Documents\Pinboard\Text Clips.pinboard`
- 本机设置与故障恢复：`%LocalAppData%\Pinboard`

画板目录可在设置中更改。程序不会依赖开发者电脑上的盘符或安装路径；便携版可放在任意有写入权限的位置。

## 第一次使用

1. 双击 `Pinboard.exe`。点击窗口右上角关闭会隐藏到系统托盘并继续在后台接收截图；之后可再次启动程序或点击系统托盘图标重新打开。
2. 以后像平时一样按 PixPin 的 `Ctrl+Alt+A`，并在截图后按 `Enter`、`Ctrl+C` 或点击复制图标。
3. Pinboard 会接收这一次普通截图，并自动以两列瀑布流错开放进当月画板；不再需要粘贴到微信或 Pinboard。
4. 取消截图或不复制图片时，不会生成空白内容。可选的 `Ctrl+Alt+P` 自定义动作只用于“Pinboard 尚未运行时也自动启动接收器”的场景。
5. 如需收集复制的文字，请先在设置中明确开启。开启后，普通文字会进入左侧独立的 `Text Clips` 分栏；每月一张画板，卡片按时间生成并纵向排列。

Pinboard 默认会在登录 Windows 后静默启动到系统托盘，以便普通 PixPin 截图随时能被自动收录。可在“设置 → Screenshot receiving / 截图接收”中关闭“Start Pinboard when I sign in / 登录 Windows 时启动 Pinboard”。

点击左侧“＋ 项目”可独立创建项目；空项目也会保留在侧栏。每个项目右侧的“＋”会直接在该项目中新建画板。点击画板右侧的“⋯”或右键画板，可直接“设为截图接收目标”；固定目标会以紫蓝炫光边框和“接收中”标记显示，在其菜单中可恢复按当前月份自动保存。该菜单还可修改显示名称、移动项目、归档、在资源管理器中定位真实文件或复制真实路径；“移到回收站”不会直接永久删除文件。归档画板会进入侧栏底部默认收起的“Archive / 归档”，可通过“恢复到项目”放回任意正常项目。修改显示名称只更新 `.pinboard` 内部元数据，不会重命名或移动磁盘文件。侧栏按项目名和显示名称固定排序，打开画板不会改变位置。

右上角的“置顶”按钮可随时控制 Pinboard 是否保持在其他窗口上方。

详细说明见 [docs/使用说明.md](docs/使用说明.md)，快捷键见 [docs/快捷键.md](docs/快捷键.md)。

## 数据与隐私

- 不需要账户，不含遥测，不自动上传，不访问云端。
- 全新安装默认关闭文字收集，需在设置中明确开启。升级时会保留已有的显式开关选择。
- 可选隐私过滤会跳过常见凭据、验证码、银行卡号和指定应用，但它只是尽力减少误收，不能保证识别所有敏感内容。
- `.pinboard` 和本机设置没有由 Pinboard 自行加密；敏感资料应配合 Windows 账户保护与磁盘加密。
- WebView2 只加载程序内置页面，外部网络请求会被桌面外壳阻止。
- 图片首次进入时转为 WebP，默认质量 80；后续保存不重复压缩。
- OCR 使用 Windows 本机的简体中文与英语识别引擎。
- `.pinboard` 是 SQLite 单文件；移动项目时图片、场景与索引会随文件一起移动，复制单个文件也可完整备份。

## 构建

需要 Windows、项目所支持的 .NET SDK、Node.js 与 WebView2 Runtime。在 PowerShell 中运行：

```powershell
.\scripts\build-release.ps1
```

脚本会依次执行前端测试与构建、依赖审计、.NET 测试和 Windows x64 发布；任一步失败都会停止。成品位于 `artifacts\portable`。

## 技术组成

- .NET WPF、WebView2、Microsoft.Data.Sqlite、SkiaSharp
- React、TypeScript、官方 `@excalidraw/excalidraw`
- Windows.Media.Ocr（离线）

第三方许可见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。

Pinboard 本身采用 [MIT License](LICENSE)。参与开发前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)；安全问题请按照 [SECURITY.md](SECURITY.md) 私下报告。
