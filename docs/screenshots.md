# Public demo screenshots / 公开演示截图

The README images are captures of the running WPF + WebView2 application. All cards and source images are synthetic fixtures, not the installed user's library or clipboard history. The demo window's folder label is replaced with `Documents · Pinboard Demo` before capture; no user path is published.

README 中的图片来自实际运行的程序。卡片文字与图片均为独立测试资料，不读取用户资料库或剪贴板历史；截图前将演示窗口的路径标签替换为通用名称。

## Reproduce / 重新生成

On an interactive Windows desktop, first build the frontend, then run the isolated smoke harness with a **new, empty output directory**:

```powershell
dotnet run --project tools/Pinboard.Smoke/Pinboard.Smoke.csproj -c Release -- artifacts/demo-screenshots
```

The harness creates its own library and preferences in that directory, exercises real canvas loading, switching, saving, sleeping, and waking, then writes:

- `screenshot-inbox.png`: two sample images arranged on the infinite canvas.
- `text-library.png`: timestamped text cards and a custom text folder.
- `compact-library.png`: the same application at a narrower window size.

It does not change the installed application's settings or copy test strings into the system clipboard. Inspect all three images before copying them into `docs/images/`. Only PNG screenshots and the application logo belong there; never publish generated `.pinboard`, settings, logs, or recovery files.

测试会在指定目录内创建独立资料库和设置，完成后关闭自己的窗口。发布前需逐张检查截图；只将 PNG 与程序 Logo 放入 `docs/images/`，不要上传生成的画板、设置或日志。
