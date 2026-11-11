# 快捷键首次唤醒优化

日期：2026-11-11。范围：窗口首次控件初始化和主界面快捷键展示，不包含 Provider 响应或 OCR 模型加载。

## 实施

- 主界面启动后，以 `ApplicationIdle` 分步准备剪贴板历史、资料库和截图控件。资料库读取继续在后台进行，快捷键请求与预加载共享读取。
- 剪贴板与资料库保留未显示的窗口，首次打开复用；截图只使用 32×32 虚构位图加载控件，随即关闭，不截取真实桌面。
- 预加载不调用 Show，不创建 HWND，不激活窗口；隐藏窗口的定时器保持停止。捕获会话和退出期间跳过准备；版本检查防止已取消请求创建窗口。
- 主界面快捷键入口直接结束 120 ms 页面过渡，立即呈现内容。
- 未新增依赖、设置、存储格式或 migration；未替换日用程序、提交或发布。

## 测量边界

相同机器、虚构数据、WPF 控件构造与离屏布局；基线先初始化主界面，与实际启动顺序一致。单次采样如下，并非按键到屏幕完整首帧延迟或百分位保证：

| 控件 | 原先首次构造及布局 | 空闲准备后首次布局 |
| --- | ---: | ---: |
| 剪贴板历史 | 52.3 ms | 3.1 ms |
| 资料库 | 47.4 ms | 1.8 ms |
| 截图翻译 / OCR 选区 | 17.1 ms | 3.7 ms |
| 截图工具 | 23.8 ms | 10.3 ms |

独立进程从未加载同类 WPF 控件时，也测得 297–705 ms 的首次初始化；这些数值不能视为实际程序启动后每个快捷键都有的延迟。预加载移动了初始化时机，并未消除这些工作；会提前占用两个窗口的内存。程序刚启动、准备未完成或准备失败时，仍走正常打开路径。

## 验证

- `ScreenTranslate.UiTests` 的 `ProductShellStartsWithCaptureAndKeepsAllDestinationsReachable` 通过下列环境变量选择专项：`SCREEN_TRANSLATE_FIRST_OPEN_ONLY=1`、`SCREEN_TRANSLATE_LIBRARY_QUICK_ACCESS_ONLY=1`、`SCREEN_TRANSLATE_FRONTEND_PERFORMANCE_ONLY=1`、`SCREEN_TRANSLATE_CLIPBOARD_POPUP_ONLY=1`、`SCREEN_TRANSLATE_SNIP_ONLY=1`。一次仅设置一个变量。
- 首次打开专项覆盖控件计时、默认准备流程、无 HWND/不可见/无运行计时器、窗口复用、等待中退出、读取失败恢复，以及预加载期间正常打开共享读取。
- 主界面快捷键动画断言先失败，修复后通过；检查唤醒后 Opacity 为 1、位移为 0、无动画缓存，并回归无标题栏、最小化还原和常规入口。
- 相关单元测试 171 项通过；过滤范围为 `HotkeyBindings|HookThread|ClipboardHistory|Library|ScreenCapture|Snip`。
- 剪贴板扩展专项在并行构建期间曾失败一次，单独重跑通过；第一次失败未留下断言详情，间歇失败原因未确认，不能表述为全套稳定通过。
- 未验证实体键盘到真实屏幕首帧、长期空闲后的换页、低速机器、多显示器性能和日用版本实际生效。

构建使用独立的 `artifacts/hotkey-first-open-build` 和 `artifacts/hotkey-first-open-unit-build`。性能输出在前者 `bin/ScreenTranslate.UiTests/debug/artifacts/first-open-all.txt`；专项 TRX 在 `artifacts/hotkey-first-open-results`。依赖使用本机缓存恢复。`SCREEN_TRANSLATE_FIRST_OPEN_KIND` 可选 `shell/clipboard/library/capture/screenshot` 分进程测量；`SCREEN_TRANSLATE_FIRST_OPEN_COLD=1` 用于主动跳过预加载的基线实验，不用于通过验收。

官方依据：[.NET 8 Dispatcher.Yield](https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatcher.yield?view=windowsdesktop-8.0)，2026-11-11 经指定代理核对；空闲优先级只是调度顺序，单个控件构造期间仍在 UI 线程执行。
