# 剪贴板面板闪退与局域网接收修复

2026-11-17。源仓库基线 `cbc9491`，本轮工作区修复；未提交、发布或替换日用安装版。悬停预览和资料库分类的并行改动不属于本轮交付。

## 实际崩溃证据与修复边界

Windows Application Error 记录：2026-11-17 11:49:17，日用版 0.3.42，`coreclr.dll`，`c0000005`。使用局部安装的 dotnet-dump 8.0.547301，读取本机既有转储 `ScreenTranslate.exe.36308.dmp`，仅检查崩溃线程调用栈，不输出堆对象、复制内容或凭证。

关键栈路径：

```text
GlobalHotkey.WndProc
ClipboardHistoryWindow.Toggle / Open / ShowPanel
UIElement.Focus
TipTsfHelper.Show / InputPane.TryShow
ImmComposition.OnWmImeRequest_ReconvertString
Marshal.PtrToStructure<RECONVERTSTRING>
```

该崩溃发生于程序化聚焦搜索框时的输入面板/IME 重入，不是 `LanNode` 接收失败后主动退出。此前日志中的剪贴板捕获 `COMException 800401D3` 是另一条失败记录，不能仅凭先后顺序认定其导致本次崩溃。

核对 .NET/WPF v8.0.31 上游 [UIElement.Focus](https://github.com/dotnet/wpf/blob/v8.0.31/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/UIElement.cs) 和 [ImmComposition](https://github.com/dotnet/wpf/blob/v8.0.31/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Documents/ImmComposition.cs)：前者在 `Keyboard.Focus` 后额外执行 `TipTsfHelper.Show`，后者直接转换请求的原生指针。面板的程序化聚焦改用 `Keyboard.Focus`，保留 IME 和键盘搜索，不主动唤起触摸输入面板；没有禁用中文输入法或增加全局吞异常。

这修复已定位的主动调用入口，不代表已修复 Windows/WPF 对任意无效 IME 请求的处理。第三方输入法、触摸操作与日用版本仍需实际验收。

## 同步接收改动

- 原来的主 UI 线程 `Clipboard.SetDataObject(copy: true)` 改为后台 STA 原生发布，复用 `LibraryClipboard.SetText` 与 `ScreenshotClipboardImage.SetImage`。
- 文本发布为 Unicode；图片同时发布原始 PNG 和可供普通应用粘贴的独立 DDB。来源格式与正文持有同一剪贴板锁，避免先发布未带防回环标记的正文；来源消费者兼容旧字符串与新 UTF-8 流。
- 成功回执前读回核对来源、正文、图片 RGB 和读取期间的剪贴板序号。读回占用只重读，不重复发布；不一致按失败处理。读回成功不保证之后其他应用不会再替换系统剪贴板。
- 写入和读回的占用各最多尝试 6 次，间隔 60 ms。取消、暂停/恢复、关闭及连接变化使旧接收失效；多个 LAN 接收者共享有界串行写入。
- 无效 UTF-8、包含 NUL 的文本不修改剪贴板并返回失败；后续有效消息仍能接收。
- 剪贴板 HRESULT 单独显示为剪贴板问题，不再误报网络中断；接收失败只记录异常类型与 HRESULT，不记录正文、设备名、路径或原始异常消息。
- `SyncIssue.Clipboard` 追加到枚举末尾，保留已有 JSON 数值含义；读取、活动显示和序列化沿用现有契约，不涉及数据库或 migration。

文件确认、校验、暂存、重名保护与取消协议未改。没有新增离线补传、断点续传或无限重试。

## 回归入口

```powershell
dotnet test src/OverTranslate.Tests/OverTranslate.Tests.csproj --no-restore --filter 'FullyQualifiedName~LanClipboardRecoveryTests|FullyQualifiedName~LanClipboardWriterTests|FullyQualifiedName~LanTransferTests|FullyQualifiedName~LanFileCancellationTests|FullyQualifiedName~LanBundleTests|FullyQualifiedName~LanMultiplexTests|FullyQualifiedName~LanReconnectTests|FullyQualifiedName~LanClipboardPriorityTests|FullyQualifiedName~SnipClipboardTests|FullyQualifiedName~ClipboardSourceTests'

$env:SCREEN_TRANSLATE_LAN_CLIPBOARD_NATIVE='1'
dotnet test src/ScreenTranslate.UiTests/ScreenTranslate.UiTests.csproj --no-restore --filter FullyQualifiedName~LanClipboardCaptureTests
Remove-Item Env:SCREEN_TRANSLATE_LAN_CLIPBOARD_NATIVE

$env:SCREEN_TRANSLATE_CLIPBOARD_INTERACTION_ONLY='1'
dotnet test src/ScreenTranslate.UiTests/ScreenTranslate.UiTests.csproj --no-restore --filter FullyQualifiedName~ProductShellStartsWithCaptureAndKeepsAllDestinationsReachable
Remove-Item Env:SCREEN_TRANSLATE_CLIPBOARD_INTERACTION_ONLY
```

真实剪贴板测试默认显式跳过，开启上述环境变量才运行；会用合成文本和图片改变系统剪贴板。验证包括：真实 TLS 回环接收、占用后否定回执、原连接重试同一消息、来源与防回环、暂停/恢复淘汰旧接收、非法文本不覆盖原内容、发布线程退出后的独立 PowerShell 进程逐像素读取图片。

本轮核心测试因并行分类开发曾临时排除尚未完成的 `PromptClassificationTests.cs` 编译项，使用忽略目录下的 `isolated-tests.targets`，没有修改该测试或项目编译配置。结果只代表上述定向范围，不代表整库测试全部通过。

本轮结果：核心定向 125 项、剪贴板采集/接收 13 项（已启用真实系统专项）、面板交互 1 项 WPF 入口全部通过，无跳过。交互入口内部包含重复打开、首次唤醒、慢存储、取消及键盘动作等场景。`git diff --check` 通过。交付审查在该本机范围为 `PASS_WITH_RISKS`；日用安装版和实体双机未验收。

## 剩余边界

- 未更新安装版、未进行真实双机、长时间压力或全部第三方输入法组合验收。
- 系统同步调用不能由取消令牌强行终止；后台 STA 不是进程级隔离，不能承诺任意原生故障均不退出。
- 文件收件成功但最终回执丢失时仍可能显示发送失败；重发可能产生编号副本。断流仍须重传。
- 自动剪贴板同步仍只处理文本/图片，纯文件复制仍需显式发送；离线新复制不排队，发送重试预算仍约 20 秒。
