# 文件接收提醒与交互检查（2026-11-11）

> 后续已按用户要求改为标签页内接收，见 [页内收件记录](File-Receive-Page-2026-11-11.md)；下文三项体验建议已补齐对应核心功能，当前行为与边界见 [文件收发补齐报告](File-Transfer-Completion-2026-11-11.md)。下文为上一轮独立弹窗方案的记录，不表示当前功能缺失。

## 本轮完成

- 收件窗口独立于后台配对窗口，避免所属窗口隐藏、最小化或位于后台时影响提醒。
- 收件窗口进入 Windows 置顶层，在任务栏保留入口，窗口标题显示发送设备，首次显示时播放系统提示音。
- 传输大小显示为字节、KB、MB 或 GB；明确提示确认前不会接收文件。
- 保存目录选择器从现有接收目录设置打开；该目录不存在时使用系统默认位置。仍须逐批选择目录并确认接收，不自动收件。
- 保留请求取消、超时、断线时关闭弹窗的既有路径。

## 已执行验证

- 新增 WPF 回归先在原实现上失败：设备窗口隐藏时，收件窗口未进入置顶层。
- 修复后，Debug 与 Release 的同步反馈聚合用例均通过（各 1 个）；覆盖设备窗口隐藏、显示、最小化三种状态，实际 HWND 的 `WS_EX_TOPMOST`、任务栏入口、取消后关闭，以及 0 字节至 64 GB 的大小显示。
- 同步反馈聚合用例也运行既有回环文件夹/文件发送、拒收、取消、空目录保留和接收设备断线保护。
- 辅助窗口设计聚合用例通过（1 个）：明暗主题、长内容滚动、最小窗口和按钮可见性。
- 核心定向命令 `dotnet test src/OverTranslate.Tests/OverTranslate.Tests.csproj --no-restore --filter 'FullyQualifiedName~Lan|FullyQualifiedName~SyncActivity'`：397 通过、0 失败、2 跳过。筛选亦匹配到名称含 Language 的测试；两项跳过来自 OCR 并发测试，不能称全部测试通过。
- `git diff --check` 通过；独立 win-x64 self-contained Release 构建通过。

## 后续体验优化建议（尚未实施）

| 优先级 | 已确认的交互限制 | 建议 | 当前依据 |
| --- | --- | --- | --- |
| 高 | 从同步记录重新发送时，文件选择框关闭后，结果只写入后台传输窗口的状态文本 | 在发起重试的窗口显示完成、拒收或失败结果，并提供返回记录的入口 | `SyncActivityWindow.Retry_Click` → `LanTransferWindow.ChooseAndSendAsync` → `SendFilesAsync` |
| 中 | 待发送项目只提供整批清空，移除一个误选项目需要重新添加 | 支持删除选中项目及 Delete 键，保留其余项目 | `FileSendWindow.xaml`、`FileSendWindow.xaml.cs`、`ClipboardHistoryWindow.Sync.cs` |
| 中 | 请求只提示“5 分钟后过期”，未显示剩余时间 | 显示实际请求截止时间对应的倒计时；超时原因在记录中明确展示 | `FileReceiveWindow`、`LanNode` 的 5 分钟确认超时 |

## 验证边界与使用

- 本次检查围绕跨电脑文件发送、接收、保存目录与同步记录；不代表整个应用所有页面的体验验收。
- 源码及上述定向自动验证通过；真实双机、独占全屏应用、混合 DPI、多显示器、系统锁屏及提示音实际可听性尚未验证。真实双机验收为 BLOCKED。
- 独立构建位于 `artifacts/file-receive-attention-20261111/app/`，压缩包解压后运行其中的 `ScreenTranslate.exe`；不能只复制单个 exe。便携构建不含个人配置或配对资料。
- 当前日用程序没有替换，没有提交、推送或发布。接收端必须运行本轮构建，提醒改动才会生效。
