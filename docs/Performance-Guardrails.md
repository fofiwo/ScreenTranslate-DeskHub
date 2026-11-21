# 性能实现与回归约束

核查日期：2026-11-14。源码基线：`b3cdf22`。本文记录当前已有的性能机制、正确性边界和对应验证入口；历史任务报告保留各自当时的结论，不能代替当前实现。

**本次核查发现一项已复现的 OCR 缓存失效缺陷，尚未修复。** 下列机制按当前源码核对，本轮实际执行与跳过的回归范围见末节；涉及真实画面、GPU、长期运行和日用安装版的效果仍有限制，不代表全产品性能验收通过。

## 后续开发如何使用

修改窗口唤醒、列表刷新、剪贴板、资料库、OCR、持续翻译或局域网同步时，先阅读对应小节和“核查发现”，定位现有实现与测试，再决定是否调整。交付时说明影响到的机制、运行的回归和未验证的边界；改变取舍后同步更新本文。

需要守住的是以下行为，不是永久冻结某个实现或数值：

- UI 事件只做必要的界面/STA 工作；读盘、锁等待、编码及推理通过已有后台路径执行。`async` 或 `Dispatcher.BeginInvoke` 本身不证明工作离开了 UI 线程。
- 连续请求合并到有界工作中；发布时检查请求版本和窗口/会话生命周期；关闭或取消后的迟到结果失效。
- 缓存复用要求所有影响结果的输入仍有效；配置或数据变化须失效。界面旧快照只用于展示，写入沿用存储层的重读与冲突检查。
- 减少工作量时保留功能正确性、持久化、权限、取消及失败反馈。复制成功、保存成功、粘贴成功分别判断。
- 并发数、图像尺度、门控阈值和轮询间隔是有适用条件的取舍；调整时同时验证响应、资源消耗和识别质量。
- 将冷启动、热唤醒、UI 输入延迟、数据就绪、OCR/Provider 耗时分开测量。合成夹具的单次数字不作为真实设备的统一 SLA。

## 1. 首次唤醒与窗口复用

已有实现：启动先发起剪贴板存储后台加载；`ShortcutUiWarmup` 在 `ApplicationIdle` 分步准备截图、剪贴板和资料库控件。截图使用合成位图及离屏布局；后两者保留未显示窗口供首次打开复用。资料库预加载和正常打开共享冷读任务。主界面快捷键入口结束页面过渡，恢复正常透明度、位移并释放动画缓存。

保留约束：准备过程不 `Show`、不创建 HWND、不抢焦点、不捕获真实桌面、不写系统剪贴板；隐藏窗口计时器停止。捕获或退出期间跳过准备，失败后正常入口可以重试。

适用边界：预热移动了初始化成本，并提前占用窗口内存；空闲优先级不使控件构造自动变成后台工作。剪贴板 `Open` **仍先等待 `StoreAsync`**；“不等列表筛选完成就显示”只发生在取得 store 之后，不能写成“磁盘冷读未完成也保证立即显示”。

实现：[ShortcutUiWarmup](../src/OverTranslate/Services/ShortcutUiWarmup.cs)、[ClipboardHistoryStoreLoader](../src/OverTranslate/Services/ClipboardHistory/ClipboardHistoryStoreLoader.cs)、[ShellWindow](../src/OverTranslate/Views/Shell/ShellWindow.xaml.cs)。回归：[FirstOpenPerformanceRegression](../src/ScreenTranslate.UiTests/FirstOpenPerformanceRegression.cs)、[FrontendPerformanceRegression](../src/ScreenTranslate.UiTests/FrontendPerformanceRegression.cs)。

剪贴板面板程序化聚焦（2026-11-17）：搜索框使用 `Keyboard.Focus` 保留键盘与 IME 输入，不使用会主动唤起触摸输入面板的 `UIElement.Focus`。0.3.42 日用版转储确认后者在快捷键唤醒时重入 `ImmComposition.OnWmImeRequest_ReconvertString`，于原生指针转换处发生访问冲突；不能将此退出归因于同步否定回执，也不能靠吞异常恢复。`SCREEN_TRANSLATE_CLIPBOARD_INTERACTION_ONLY` 的首次唤醒测试不再禁用搜索框 IME；真实第三方输入法组合仍须单独验收。

## 2. 剪贴板列表、图片和连续捕获

历史条目向外拖拽（2026-11-18 更新）：正文按下后超过系统拖动阈值才开始拖拽；按钮和滚动条不作为拖拽起点。拖动、取消或准备失败后的松手不触发点击粘贴，未超过阈值仍沿用点击动作。文本保留 Unicode/HTML/RTF，文件和文件夹沿用原路径；纯图片提供位图与原始 PNG 字节的临时副本，拖拽只允许 Copy，不移动历史附件或源文件，不写系统剪贴板、不更新复制时间。读盘、解码和副本写入在后台完成；准备期间松手、隐藏窗口、筛选切换或关闭后不再启动拖拽。临时副本保留到窗口关闭，兼容目标在 Drop 返回后读取文件；异常退出或清理失败可能留下临时文件。默认 `SCREEN_TRANSLATE_CLIPBOARD_DRAG_ONLY` 验证格式、原字节、副本隔离和缺失文件；额外设置 `SCREEN_TRANSLATE_CLIPBOARD_DRAG_NATIVE=1` 才运行真实鼠标、取消、提前松手和 OLE 输入框/文件接收区专项。2026-11-18 原生专项通过：小幅移动、取消、准备期间松手，以及文本和 PNG 向测试创建的 WPF 接收窗口执行 OLE 拖放；文本按原样保留 LF，测试不再错误要求 CRLF。运行原生专项期间须暂停其他鼠标操作；额外输入曾使“小幅移动”夹具实际移动超过阈值。该结果不代表资源管理器、第三方应用或日用安装版已验收，默认 CI 未开启原生专项。纯文本不导出为 .txt，能否接收某种拖放格式由目标应用决定。

文件拖放（2026-11-17）：选中“设备与同步”页时保持置顶且失焦不收起，使用户可切到资源管理器选择并拖入文件；此状态不改变图钉选择，切回普通历史分类后恢复原有失焦规则。再次按快捷键直接收起同步页，包括当前焦点在其他窗口时；关闭按钮和 Esc 也可主动收起。接收请求结束及图钉切换也须遵守当前页面的保留条件。回归：`SCREEN_TRANSLATE_CLIPBOARD_PIN_ONLY`、`SCREEN_TRANSLATE_SYNC_FEEDBACK_ONLY`（WPF 失焦、合成拖放及回环收发，不代表真实资源管理器拖拽验收）。

已有实现：存储按成功变更失效排序快照，未变时复用只读快照；后台筛选和视图准备由每窗一个刷新循环执行。搜索输入使用 100 ms 防抖，显示/分类/数据刷新直接执行；版本过期结果不发布，条件切换期间阻止操作旧结果。无变化时保留列表与条目，时间标签只通知已经实现的可见卡片。

图片按需后台读取/解码，缩略图并行上限为 2，弱引用允许滚出视口后的图片回收；预览检查请求是否仍有效。WPF 列表保持内容滚动、虚拟化和 `Recycling`。读取系统剪贴板保留在调用线程，冻结图像后的 PNG 编码及历史写入放后台；慢捕获期间的连续通知合并为后续一次最新读取，并检查剪贴板序列号，退出时排空已接受的工作。

图片悬停预览上限为 1280×960 WPF 逻辑单位（2026-11-17，原 640×480 的宽高各两倍），保持等比显示；实际尺寸仍受图片自身尺寸及 WPF 弹窗屏幕限制影响。此调整仅改变显示上限，沿用已有后台加载、解码限制和关闭后释放机制。

保留约束：COM/STA 读取与纯数据处理分开；列表复用仍须反映置顶、复制时间、删除和新增。合并捕获不承诺保存每一条快速变化的中间内容。存储成功前不提前发布新快照。

保存故障恢复（2026-11-15）：主图片与元数据提交成功后先同步内存记录，恢复备份失败单独提示，不能回滚已提交图片或阻止后续复制。当前进程记录待备份条目，后续保存/置顶/复制时间更新时按最新记录补写；一次备份失败即停止本轮补写，避免每次遍历整份失败积压。删除移除待备份条目，目录切换成功后以新位置的完整备份为准。待补写集合仅在当前进程内保留，重启不保证自动补齐之前缺失的备份；主记录仍可正常读取。主文件写入失败继续报告失败，不将其当作备份警告。

捕获与主文件存储失败分别提示，并只记录阶段、异常类型和 HRESULT，不记录复制内容、路径或异常正文；成功后清除对应旧错误提示，不覆盖其他操作反馈。系统同步调用长期不返回仍不能通过强行释放共享锁安全恢复，本次未引入并行绕过或假超时成功。回归入口：`ClipboardHistoryStoreTests`、`ClipboardHistoryWriteQueueTests`、`SCREEN_TRANSLATE_CLIPBOARD_STORAGE_ONLY`（含捕获失败、主目录失败、备份失败与后续恢复的合成链路，不访问真实系统剪贴板）。

适用边界：UI 虚拟化不是数据分页。当前仍加载全部历史元数据，筛选成本随数据量增长；弱引用与解码并发限制也不是整个进程的内存上限。大富文本解析、系统剪贴板调用及冷盘成本仍需独立测量。

实现：[ClipboardHistoryStore](../src/OverTranslate/Services/ClipboardHistory/ClipboardHistoryStore.cs)、[ClipboardHistoryRuntime](../src/OverTranslate/Services/ClipboardHistory/ClipboardHistoryRuntime.cs)、[ClipboardHistoryWindow](../src/OverTranslate/Views/ClipboardHistory/ClipboardHistoryWindow.xaml.cs) 及其 [.xaml](../src/OverTranslate/Views/ClipboardHistory/ClipboardHistoryWindow.xaml)。回归：[ClipboardPerformanceRegression](../src/ScreenTranslate.UiTests/ClipboardPerformanceRegression.cs)、`ClipboardHistory*Tests`。

## 3. 资料库、复制与设置页面

已有实现：搜索/标签/追加列表使用界面快照；激活、同步通知和写入完成后后台重载。重载合并请求、忽略过期结果，只比较消费字段，不为判断变化序列化整份资料；内容不变时保留条目与选择。提示词分批追加 30 条并使用控件虚拟化；模型搜索与提示词搜索分别刷新自己的列表。

复制文本使用 Win32 Unicode 提交，避免 OLE 延迟渲染和同步 Flush；只对占用做有界异步重试。同条目连点合并，新请求及离开/隐藏取消旧重试。提示词复制成功后先排队保存次数，再立即收起资料库，不等待写盘；复制失败保留界面重试。次数顺序后台保存，隐藏时不排序，再次打开时恢复排序；可见期间仍使用约 600 ms 停顿后的排序机制，其他复制操作保留即时反馈。页签、资料操作、AI 整理及模型目录读取也使用后台路径；退出等待已排队的相关保存。

主窗口统计后台读取、合并激活请求；AI 整理设置后台加载、合并在途读取、失败可重试并保留草稿。持续翻译页面无设置变化时复用选项和选中项，不重复触发保存。

任务分类（2026-11-17）：分类/标签/搜索复用当前快照与 30 条追加机制，不在输入时读盘。新建先保存再异步分类；元数据整理合为同一请求，批量每次最多 10 条、顺序请求，完整输入超过 60,000 字符则拒绝，不截断正文。单条与单批分类请求截止时间均为 5 分钟，底层 HTTP 上限同步调整；保留主动取消和窗口生命周期取消，模型目录获取与独立元数据整理保留各自的较短截止时间。人工分类和内容变更拒绝旧结果覆盖；分类写入后台重读、合并并等待退出。单击条目复制完整正文，方向键选择并查看详情，按钮或 Enter 也可复制；按钮事件独立处理，避免一次点击重复复制。同步协议升为 3，提示词与模型资料同步要求两端均为支持分类的新版。回归增加 `PromptClassificationTests` 与 `SCREEN_TRANSLATE_PROMPT_CATEGORIES_ONLY`，并保留紧凑布局、保存反馈、复制次数和刷新专项。[机制与验证边界](Prompt-Categories-2026-11-17.md)。

保留约束：UI 快照不参与覆盖整库写入。继续使用 `LibraryStore` 写前重读、互斥锁、原子替换、字段保护和冲突检查；“已复制但次数保存失败”须单独报告，自动收起后发生的失败在下次打开时显示，不抢回焦点。正文筛选和 UI 绑定仍有规模成本，30 条追加不是磁盘分页。

实现：[LibraryWindow](../src/OverTranslate/Views/Library/LibraryWindow.xaml.cs)、[LibraryClipboard](../src/OverTranslate/Services/Library/LibraryClipboard.cs)、[LibraryStore](../src/OverTranslate/Services/Library/LibraryStore.cs)、[ShellWindow](../src/OverTranslate/Views/Shell/ShellWindow.xaml.cs)。回归：`LibraryFilterPerformanceTests`、[FrontendPerformanceRegression](../src/ScreenTranslate.UiTests/FrontendPerformanceRegression.cs)、[RefreshPerformanceRegression](../src/ScreenTranslate.UiTests/RefreshPerformanceRegression.cs)、资料库 UI 专项。

## 4. 截图渲染与图片复制

已有实现（2026-11-15 更新）：复制时先冻结本次选区/标注状态并隐藏窗口，后台 STA 消息泵完成渲染、原生位图提交及异步重试；没有标注时复用选区图像。使用截图窗口已有 HWND，在打开剪贴板前通过 `CreateDIBitmap` 准备独立 DDB，再一次性提交 `CF_BITMAP`，避开 WPF/OLE 延迟渲染、Flush 及重复通知。读回使用 Win32 位图快照，避免 OLE 的隐藏同步重试；线程启动与回收不等待调用方 UI 上下文，工作结束后退出。

保留约束：截图/取色/贴图真实写入继续串行、可取消、有界重试；成功须读回核对可见 RGB，分块比较以控制附加分配。读回仅被占用时只重读，避免再次发布通知。失败保留选区和标注并允许重试。合成预热不占用真实写入互斥锁。

拖拽框选时复用尺寸标签，按最终截图的像素裁剪规则计算宽高。初次拖拽只更新选框和标签，松开鼠标后才创建裁剪位图。尺寸、反向拖拽和屏幕边缘布局由 [SnippingRegression](../src/ScreenTranslate.UiTests/SnippingRegression.cs) 覆盖。

适用边界：进入系统同步调用后不能被令牌强行中止；线程迁移不等于它有固定完成时间。不能为提速删掉核对，也不能用每次重写剪贴板解决读回占用。`CF_BITMAP` 的 GDI 所有权须交给系统，发布窗口、工作线程甚至进程退出后其他进程仍须能读取；本机原型曾暴露直接转交 DIB section 的跨进程失效，最终使用独立 DDB 并保留跨进程回归。图片按截图既有契约核对可见 RGB，不承诺透明图编辑格式；低色深/RDP 桌面及其他程序持续抢占仍需单独验证。取色和贴图复制沿用原路径及共享写入互斥。

实现：[ScreenCaptureWindow.Snip / SnipClipboard](../src/OverTranslate/Views/Capture/ScreenCaptureWindow.Snip.cs)、[ScreenshotClipboardImage](../src/OverTranslate/Services/ScreenshotClipboardImage.cs)。回归：[SnipClipboardTests](../src/OverTranslate.Tests/SnipClipboardTests.cs)、[SnipCopyPerformanceRegression](../src/ScreenTranslate.UiTests/SnipCopyPerformanceRegression.cs)、[SnipImagePublicationRegression](../src/ScreenTranslate.UiTests/SnipImagePublicationRegression.cs)。

本轮测量区分首次可读和任务结束：本机合成 4K、每次框选停顿 1.5 秒、独立 STA 消费者读取的五次热样本为 63.7/64.6/58.3/64.2/55.6 ms，中位数 63.7 ms；不代表所有软件、设备或连续抢占场景都能在此时间粘贴。原构建在同一最终夹具下直接复制失败。另一次原生监测记录 `wetype_server` 在 4K 发布后约 193–960 ms 独占剪贴板；软件内部提速不能解除第三方的锁，快速连续截图仍可能耗尽重试。未关闭第三方功能、未更新日用安装版。

真实剪贴板专项：设置 `SCREEN_TRANSLATE_SNIP_COPY_ONLY=1`，运行上述 UI 项目的 `ProductShellStartsWithCaptureAndKeepsAllDestinationsReachable` 入口；该专项会显示合成截图窗口、写入合成图片，并启动无窗口 PowerShell 消费者校验关闭截图 HWND 后的完整 RGB。包括即时隐藏、异步响应、取消/失败、奇数尺寸裁剪、多像素格式、历史 PNG 编码、WPF/WinForms/跨进程兼容及占用失败的 GDI 资源回收。默认全套入口不触发新增真实剪贴板专项。本机探针与输出在 `artifacts/snip-latency-20261115/`。

## 5. OCR 引擎复用、预算和模型生命周期

已有实现：`AppServices` 共用 OCR/翻译服务，避免各窗口重复创建引擎与连接资源。CPU OCR 同时约束每次推理线程数和并行槽数；实时调用没有槽时跳过本次轮询，按需识别可以等待。模型按需加载，持续会话预加载并保持温热，空闲释放；截图开始前先让持续翻译让出资源。

日文竖排漫画另有 DirectML 路径：单页占用 GPU；解码 KV cache 通过 IOBinding 留在设备上，固定 4 行批次，短批补齐，降低形状变化造成的开销。模型预热、失败记忆及空闲释放避免反复初始化；不可用时保留原有识别降级语义。

保留约束：并发预算按“线程 × 槽”一起判断；GPU 推理持有期和释放必须配对，后台任务结束前不能归还仍在使用的资源。保留设备/模型能力判断。GPU 的批次和加速收益限于该模型与硬件，不能推广为所有 OCR 的通用规则。

适用边界：见下文 OCR 并发跳过项；“截图与持续翻译任意并存仍安全”并未验证。现有模型常驻/空闲释放是延迟与内存取舍，不是越久常驻越好。漫画模型的真实 GPU 测试本轮跳过，未重新证实其加速幅度。

实现：[AppServices](../src/OverTranslate/Services/AppServices.cs)、[OcrThreadBudget](../src/OverTranslate/Services/Ocr/OcrThreadBudget.cs)、[OnnxOcrEngine](../src/OverTranslate/Services/Ocr/OnnxOcrEngine.cs)、[MangaOcrEngine](../src/OverTranslate/Services/Ocr/Manga/MangaOcrEngine.cs)、[MangaTextRecognizer](../src/OverTranslate/Services/Ocr/Manga/MangaTextRecognizer.cs)。回归：`OcrThreadBudgetTests`、`RealtimeModelLoadingTests`、`MangaModelSwitchTests`。

截图内重复 OCR 还有 `CaptureOcrReuse`，但其当前失效条件存在缺陷，见“核查发现”；这里只认可“有效输入不变时复用”的方向，不认可现有 key 已完整。

## 6. 持续翻译的变化检测、请求合并与退避

已有实现：通过帧指纹及文字带比较跳过不必识别的轮询；定期全区重扫与空闲补扫覆盖文字移位或低于差异阈值的变化。定时搜索/重扫先用低尺寸检测门控，用户主动识别及文字变化路径保留直接识别；门控忙碌的 `null` 与真正空结果区分处理。

翻译缓存 key 包含 Provider、备用策略、语言对及原文；同 key 同 generation 的在途行合并。会话翻译并发上限为 4，跨区域共用。暂停/语言切换通过 generation 拒绝迟到写入和发布，已完成且仍适用的缓存可以复用；未翻译结果不作为成功答案缓存。失败按现有策略退避，新内容和主动重读保留响应能力。背景刷新超时后不补跑积压 tick。

保留约束：变化采样、识别搜索、重扫和背景绘制是不同节奏，不能一起无条件提频。门控、降采样和比较阈值必须验证漏字/错字；后台耗时和发送并发有上限，旧结果不能覆盖新结果。

适用边界：0.30/0.40 交替检测与 0.6 分数门槛来自有限语料；注释中的“lossless”不能解释成任意文字都无漏检。检测尺度优化同时影响识别质量；800 像素缩放门槛的源码明确保留了未针对新模型重测的限制。翻译缓存也不代表同一原文在所有语境和配置下永远同译；新增模型/提示词热切换时须重新核对缓存身份及失效。

实现：[RealtimeTranslationSession](../src/OverTranslate/Services/Realtime/RealtimeTranslationSession.cs)、[RealtimeRegionState](../src/OverTranslate/Services/Realtime/RealtimeRegionState.cs)、[RealtimeGate](../src/OverTranslate/Services/Realtime/RealtimeGate.cs)、[RealtimeDetectorSize](../src/OverTranslate/Services/Realtime/RealtimeDetectorSize.cs)、[RealtimeTranslationCache](../src/OverTranslate/Services/Realtime/RealtimeTranslationCache.cs)、[RealtimeInFlightTranslations](../src/OverTranslate/Services/Realtime/RealtimeInFlightTranslations.cs)。回归：`RealtimeRegionStateTests`、`RealtimeGateFailureTests`、`RealtimeDetectorSizeTests`、`RealtimeTranslationCacheTests`、`RealtimeInFlightTranslationsTests`、`RealtimePublishOrderTests`、`RealtimeRetryBackoffTests`、`RealtimeBackgroundCadenceTests`。

## 7. 捕获回读与同步流量

已有实现：WGC 捕获限制昂贵 GPU→CPU 回读频率；间隔内的新帧保留最新一张并到期读取，避免静态画面一次变化后永远停留在旧帧。无人轮询时跳过；同一尺寸的帧池重建失败不逐帧重试。当前普通回读间隔为 120 ms，真实尺寸变化另有即时处理。

局域网连接将剪贴板、文件和资料库分为三条逻辑流；每通道一个未消费分块及确认机制控制背压，避免慢文件占住剪贴板消费路径。共享 TLS 连接及实际带宽仍然共用，不是三条独占物理网络。

局域网剪贴板接收（2026-11-17）：解码后由后台 STA 原生发布 Unicode 文本或 PNG + 独立 DDB，复用资料库/截图的发布代码；来源标记与正文在同一次剪贴板锁内提交，不使用 OLE Flush。读回核对来源、正文、位图 RGB 及读取期间序号，成功后才返回确认。写入占用与读回占用分别最多尝试 6 次、间隔 60 ms；读回重试不得重发正文。暂停后再恢复也使旧接收请求失效；仍在系统同步调用中的工作不能强行终止，后台线程不等于进程隔离。原生来源流兼容旧版托管字符串；日志只记录异常类型与 HRESULT。回归：`LanClipboardWriterTests`、`LanClipboardRecoveryTests`、`LanClipboardCaptureTests`；真实系统剪贴板专项需设置 `SCREEN_TRANSLATE_LAN_CLIPBOARD_NATIVE=1`，使用合成内容并校验跨进程粘贴。[本轮验证与边界](Clipboard-Lan-Receive-2026-11-17.md)。

资料库每轮结束后约 3 秒轮询；缓存本机加密状态及版本，文件指纹变化、本机写入或约 30 秒兜底重读后重新计算。连接内按对方已确认版本发送增量，无变更跳过导出/序列化；首次、重连、恢复发送完整快照，拒收或保存失败不推进版本。

保留约束：捕获节流须保住最后一帧；资源退出/重建沿用线程与释放边界。同步保留认证、长度校验、分块确认、取消、删除标记及冲突合并；文件长度和时间戳不能独立证明内容未变，所以定期重读兜底仍有必要。缓存与增量不能以丢更改换流量。

实现：[WgcFrameThrottle](../src/OverTranslate/Services/Realtime/Capture/WgcFrameThrottle.cs)、[WgcWindowCaptureBackend](../src/OverTranslate/Services/Realtime/Capture/WgcWindowCaptureBackend.cs)、[WgcMonitorCaptureBackend](../src/OverTranslate/Services/Realtime/Capture/WgcMonitorCaptureBackend.cs)、[LanMultiplexStream](../src/OverTranslate/Services/Lan/LanMultiplexStream.cs)、[LanLibrarySync](../src/OverTranslate/Services/Lan/LanLibrarySync.cs)、[LibrarySyncMerge](../src/OverTranslate/Services/Library/LibrarySyncMerge.cs)。回归：`WgcFrameThrottleTests`、`LanMultiplexTests`、`LibrarySyncOptimizationTests`；真实捕获另见 [WgcProbe](../tools/WgcProbe/README.md)。

## 核查发现：当前问题与不能固化的结论

### P2：截图 OCR 缓存未随服务配置失效（已复现，未修复）

触发：同一次截图已有识别结果，在应用内保存新的 OCR 服务配置，再对相同选区、源语言、方向和布局执行识别。

预期：使用新 OCR 配置重新识别。实际：`CaptureOcrReuse.TryGet` 返回旧块，`MainWindow.RecognizeCropAsync` 在调用新服务前提前返回。当前 key 只有截图对象、选区、源语言、方向和布局；`OcrSettingsPage.SaveOptions` 成功后只 `ReleaseModel`，没有清理这份结果缓存。应用自己的窗口切换也不会被 `LiveOverlayGuard` 当成外部切换来自动结束截图。

本轮隔离探针：注入合成旧结果，通过真实设置页保存逻辑从 Local 切到回环 HTTP，再调用真实 `RecognizeCropAsync`，得到 `Reused=true` 和旧结果。保存回调为测试替身，没有写个人设置；未调用外部服务、捕获桌面或访问系统剪贴板。本机探针保存在 `artifacts/performance-audit-20261114/probe/`，不属于仓库或长期回归测试。

修复应让 OCR 配置变化与结果缓存失效配对，例如使用配置 revision 或明确的失效通知，并补“配置变化必须重新识别”的集成回归。还须考虑在途旧请求不能重新写回新配置的缓存；不要把“只补一个 Provider 枚举”视为已经覆盖端点、模型等变化。本文不将现有 key 写成完整范式。

相关源码：[CaptureOcrReuse](../src/OverTranslate/Services/Ocr/CaptureOcrReuse.cs)、[MainWindow.RecognizeCropAsync](../src/OverTranslate/Views/MainWindow.xaml.cs)、[OcrSettingsPage.SaveOptions](../src/OverTranslate/Views/Settings/OcrSettingsPage.xaml.cs)、[LiveOverlayGuard](../src/OverTranslate/Services/LiveOverlayGuard.cs)。已有 `CaptureOcrReuseTests` 只覆盖原有 key 维度，不能拦住此缺陷。

### P3：OCR 预算的旧说明与实现不一致

`OcrThreadBudget` 顶部仍写线程与槽的乘积“保持半台机器”，后文、实现及测试已经允许 12/16 核机器分别取 3×3 / 4×3，即名义预算达到四分之三。低核数还保留最小预算例外。它是推理预算，不能等同实际 CPU 使用率。本轮保留源码，仅在本文纠正结论；不要引用过时开头建立新约束。

### 仍需保留的验证缺口

- 真冷盘读取剪贴板元数据仍可能延迟窗口创建；预加载成功与真实冷启动分别验收。
- 没有分页存储或全文索引；万条内存夹具通过不证明任意历史规模、低速磁盘和超大富文本都流畅。
- 低尺寸门控和检测尺度的质量收益只对已测语料成立；本轮有外部图片语料测试跳过，不能写成无损压缩/识别的保证。
- 截图会暂停持续翻译是现有资源互斥前提；两项跨语言并存测试明确跳过。改变这一前提前须恢复相应测试并检查运行时模型切换。
- 漫画模型/GPU 条件测试本轮跳过；没有真实双机同步、物理热键到首帧、长期空闲恢复、长时间内存/句柄压力或日用安装版验收。

## 本轮验证与后续回归入口

本轮只增加文档及本机忽略目录中的审查探针，没有修复产品源码，也没有更新安装版。

| 检查 | 本轮结果 | 能证明的范围 |
| --- | --- | --- |
| 下列筛选的核心测试 | 279 通过、0 失败、5 跳过，共 284 | 已有缓存/合并/节流/预算/存储/同步/截图复制等定向行为；不覆盖新发现的配置失效缺陷 |
| `FRONTEND_PERFORMANCE_ONLY` | 1 个 WPF 入口通过，内含多个场景 | 锁等待时输入响应、重载合并、取消/重试、草稿保留、万条记录重复打开 |
| `FIRST_OPEN_ONLY` | 1 个 WPF 入口通过 | 合成控件构造/离屏布局、准备生命周期、窗口复用及快捷唤醒动画状态 |
| `REFRESH_AUDIT_ONLY` | 1 个 WPF 入口通过 | 无变化重载不触发选中/保存、模型搜索不重建提示词列表 |
| OCR 配置切换探针 | 成功复现旧缓存返回 | 已确认缺陷，不能计为产品行为通过 |

5 项跳过：两项 `OcrEngineConcurrencyTests` 因截图让出持续翻译的设计跳过；两项 `RealtimeDetectorSizeTests` 缺少外部截图语料；一项真实漫画模型切换测试未满足模型/GPU条件。未把跳过计为通过。

本轮单机合成采样：2,000 条提示词在写锁占用场景下输入响应 72 ms；AI 设置入口 8 ms，连续加载请求合并为 1 次读取；主窗口阻塞读取场景构造 460 ms、连续摘要请求共 2 次读取。预备后剪贴板/资料库首次离屏布局分别为 3.0/1.9 ms，截图工具为 11.9 ms。它们是各自夹具的一次记录，既不是端到端首帧延迟，也不与历史不同条件样本直接比较。本机原始输出位于测试输出目录的 `artifacts/frontend-performance.txt`、`first-open-all.txt`、`refresh-performance.txt`。

复现本轮核心筛选（已有依赖；无网络还原）：

```powershell
$filter = @(
  'ClipboardHistory', 'LibraryFilterPerformance', 'LibrarySyncOptimization', 'LanMultiplex',
  'CaptureOcrReuse', 'OcrThreadBudget', 'WgcFrameThrottle', 'RealtimeTranslationCache',
  'RealtimeInFlightTranslations', 'RealtimeRegionState', 'RealtimeDetectorSize',
  'RealtimeGateFailure', 'SnipClipboard', 'RealtimeRetryBackoff', 'RealtimeBackgroundCadence',
  'OcrEngineConcurrency', 'RealtimePublishOrder', 'RealtimeFrameLoss',
  'MangaDecodeGuard', 'MangaModelSwitch'
) | ForEach-Object { "FullyQualifiedName~$_" }
dotnet test src/OverTranslate.Tests/OverTranslate.Tests.csproj --no-restore --filter ($filter -join '|')
```

UI 专项一次只设置一个 `SCREEN_TRANSLATE_*_ONLY` 变量，串行运行，避免焦点和计时互相干扰。例如：

```powershell
$env:SCREEN_TRANSLATE_FRONTEND_PERFORMANCE_ONLY = '1'
try {
  dotnet test src/ScreenTranslate.UiTests/ScreenTranslate.UiTests.csproj --no-restore --filter 'FullyQualifiedName~ProductShellStartsWithCaptureAndKeepsAllDestinationsReachable'
} finally {
  Remove-Item Env:SCREEN_TRANSLATE_FRONTEND_PERFORMANCE_ONLY -ErrorAction SilentlyContinue
}
```

本轮另外两项把变量替换为 `SCREEN_TRANSLATE_FIRST_OPEN_ONLY` / `SCREEN_TRANSLATE_REFRESH_AUDIT_ONLY`，使用同一新鲜构建及 `--no-build --no-restore`。有别的构建正在执行时使用独立 `--artifacts-path`，并先为该目录完成本地依赖还原；不要把旧产物的 `--no-build` 结果当成新源码结果。新增实际系统剪贴板/捕获/Provider 验证前明确副作用，使用合成数据和合适的测试环境。

当修改相关路径时，按上面各节补跑对应专项；未涉及的完整 UI、真实硬件和外部服务不因本文而默认宣称通过。发现阻塞、资源增长或识别质量退化时，保留触发输入、运行版本、阶段耗时、次数/分布及失败证据，避免仅凭“感觉更快”更改这些约束。

## 历史记录

- [快捷键首次唤醒](Shortcut-First-Open-2026-11-11.md)
- [截图首次使用](Screenshot-First-Use-Performance-2026-11-11.md) 及其[对抗复核](Screenshot-First-Use-Adversarial-Review-2026-11-11.md)
- [前端交互性能](Frontend-Performance-2026-11-10.md)
- [剪贴板性能](Clipboard-Performance-2026-11-09.md)及[首次唤醒调研与后续实施](Clipboard-First-Wake-Research-2026-11-10.md)
- [资料库复制响应](Library-Copy-Responsiveness-2026-11-06.md)
- [局域网同步现状](ScreenTranslate-Lan-Sync.md)

历史报告中的“未安装”“当时已通过”和当时毫秒数只描述对应任务；判断当前代码、当前设备和当前安装版时重新核对。
