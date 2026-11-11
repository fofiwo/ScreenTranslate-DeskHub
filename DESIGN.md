---
name: ScreenTranslate
description: 以屏幕翻译、剪贴板和资料库为主要入口的 Windows 桌面工具集
colors:
  rhine-blue: "#003DA6"
  rhine-blue-hover: "#00338D"
  rhine-blue-pressed: "#00286F"
  orange-yellow: "#FFBE24"
  pure-white: "#FFFFFF"
  text-primary: "#18243D"
  text-secondary: "#58647A"
  header: "#EFF2F7"
  border: "#DDE3EF"
  border-subtle: "#E9EDF5"
  accent-subtle: "#EDF2FF"
  list-bg: "#E8EDF5"
  list-hover: "#EBF0F9"
  list-selected: "#D8E5FC"
  button-border: "#D3DAE6"
  button-hover: "#F0F4FC"
  button-pressed: "#E3EAF8"
  input-border: "#B8C5DC"
  warm-bg: "#FFF0C2"
  warm-fg: "#714400"
  hero-fg: "#302400"
  hero-secondary: "#544000"
  dark-window: "#11182B"
  dark-surface: "#19233A"
  dark-rail: "#141D32"
  dark-card: "#202C46"
  dark-list-bg: "#141D32"
  dark-list-hover: "#2B3B58"
  dark-list-selected: "#2D456F"
  dark-text-primary: "#F5F7FF"
  dark-text-secondary: "#B7C4DD"
  dark-accent: "#9BB9FF"
  dark-accent-hover: "#B8CEFF"
  dark-accent-pressed: "#85A8F5"
  dark-accent-fg: "#102454"
  dark-border: "#3A4864"
  dark-border-subtle: "#2E3D58"
  dark-input-bg: "#141F35"
  dark-input-border: "#62779E"
  dark-accent-subtle: "#293F6B"
  dark-button-hover: "#304263"
  dark-button-pressed: "#3C5279"
  dark-warm-bg: "#473712"
  dark-warm-fg: "#FFD77A"
typography:
  page-title:
    fontFamily: "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Sans-Serif"
    fontSize: "28"
    fontWeight: 600
  capture-title:
    fontSize: "25"
    fontWeight: 600
  section-title:
    fontSize: "17"
    fontWeight: 600
  body:
    fontSize: "13"
    fontWeight: 400
  navigation:
    fontSize: "14"
  hint:
    fontSize: "12"
    lineHeight: "20"
  glyph:
    fontFamily: "Segoe Fluent Icons, Segoe MDL2 Assets"
    fontSize: "18"
rounded:
  control: "6"
  list-item: "8"
  navigation: "10"
  capture-panel: "14"
  content-host: "16"
spacing:
  home-horizontal: "36"
  home-top: "16"
  section-heading-bottom: "12"
  list-item-padding: "16"
components:
  button-primary:
    backgroundColor: "{colors.rhine-blue}"
    textColor: "{colors.pure-white}"
    rounded: "{rounded.control}"
    padding: "20,0"
    height: "44"
  button-primary-hover:
    backgroundColor: "{colors.rhine-blue-hover}"
  button-primary-pressed:
    backgroundColor: "{colors.rhine-blue-pressed}"
  button-secondary:
    backgroundColor: "{colors.pure-white}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.control}"
    padding: "16,0"
    height: "40"
  capture-action:
    backgroundColor: "{colors.rhine-blue}"
    textColor: "{colors.pure-white}"
    rounded: "{rounded.control}"
    padding: "28,0"
    height: "48"
  capture-panel:
    backgroundColor: "{colors.orange-yellow}"
    textColor: "{colors.hero-fg}"
    rounded: "{rounded.capture-panel}"
    padding: "28,22"
  navigation-selected:
    backgroundColor: "{colors.accent-subtle}"
    textColor: "{colors.rhine-blue}"
    rounded: "{rounded.navigation}"
    padding: "14,0"
    height: "44"
  input:
    backgroundColor: "{colors.pure-white}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.control}"
    height: "34"
---

# Design System: ScreenTranslate

## Overview

**Creative North Star: "顺手的桌面工具集"**

这是 .NET 8 / WPF 原生 Windows 应用。视觉方向是简洁、有活力、按操作顺序展开；中文优先，侧栏并列呈现屏幕翻译、截图工具、剪贴板历史和资料库，截图翻译仍为首次默认入口。莱茵蓝负责行动与选中状态，浅暖黄突出框选任务。浅色主窗口和侧栏使用 #F3F5F9，白色工作区使用独立的圆角内容层；导航与 Tab 用淡蓝底、蓝字表达选中。绿色主视觉已被用户明确否定。

本文记录已经落地的 XAML，而不是网页设计方案。Frontmatter 是本次提取的规范值；**无单位长度均为 WPF 设备无关单位（DIP，1/96 英寸），不是物理屏幕像素；逗号值采用 WPF Thickness 顺序**。组件颜色默认描述 Light，`dark-*` 对应 Dark；生产代码继续以双主题资源字典和 `DynamicResource` 为真源，不把本文颜色硬编码进视图。

**Key Characteristics:**

- 先完成语言与服务选择，再执行框选；每页有一个清晰的主要动作。
- 原生控件、两级正文颜色、适量留白与静态框选几何。
- 保留原有命名控件、事件、快捷键、延迟构造和设置持久化契约。

## Colors

### Primary

莱茵蓝 `rhine-blue` 对应浅色 `AppAccent`，用于主按钮、选中导航文字、选中指示条和链接。悬停、按下分别使用其专属状态资源。`ProductFrameAccent` 在两种主题都保持莱茵蓝，用于首页框选角标。

Dark 中 `AppAccent` 使用 `dark-accent`，标签文字使用 `dark-accent-fg`；它不是白字按钮。不要强制把深色主题的选中项改成浅色主题配色。

### Secondary

亮橙黄 `orange-yellow` 为品牌色；按用户确认，浅深主题的 `ProductHeroBg` 均使用原色 #FFBE24。其标题与说明分别使用 `hero-fg`、`hero-secondary`，不要用白字。`warm-*` 是清除提示的小面积琥珀色组合，Dark 使用对应 `dark-warm-*`。

### Neutral

Light 的 `AppWindowBg` 与 `ProductRailBg` 为 #F3F5F9，`AppSurfaceBg`、`AppCardBg` 为白色，`AppHeaderBg` 为 #EFF2F7，分别承担窗口底层、内容层和分组层。`text-primary` 用于标题及正文，`text-secondary` 用于标签、说明与首页 Aa 字样。`border`、`border-subtle` 区分普通边界与弱分隔；输入框使用单独的 `input-border`。

Dark 的窗口、工作区、侧栏、卡片分别使用 `dark-window`、`dark-surface`、`dark-rail`、`dark-card`，文字与边界也必须成套切换。新建 `AppSettings.Theme` 默认为 `Light`；已有显式 Light/Dark 选择继续保留，由 `ThemeService` 更换资源字典。

**The Semantic State Rule.** 不把品牌偏好套到所有状态色。既有成功、错误、警告、服务模式标记和浮动工具栏有独立资源；保留其语义，不把成功绿理解成绿色主视觉，也不一律改成品牌蓝。

## Typography

主文字通过 `AppFont` 动态取得字体。共享字典初值为 `Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI, Microsoft JhengHei UI, Sans-Serif`；`LocalizationService` 按界面语言替换，其中简体中文为 frontmatter 的 page-title 字体栈。不要把简体中文字体栈固定到所有本地化界面。图标使用 Windows Fluent/MDL2 字体，不以 emoji 代替。

| 层级 | 现有实现及用途 |
| --- | --- |
| 页面标题 | `PageTitle`，page-title，SemiBold |
| 框选标题 | capture-title，SemiBold，仅用于首页任务区 |
| 分节标题 | `ProductSectionTitle`，section-title，SemiBold；首页次要入口标题为 16，OCR 识别方式标题为 20 |
| 正文与字段 | body；`PageSubtitle` 与 `FieldLabel` 使用次级文字色 |
| 导航/主要按钮 | navigation；选中导航与主要按钮为 SemiBold |
| 辅助说明 | `ProductHint`，hint，自动换行；已有高级设置还保留 11 的 `FieldHint` |

没有全局字距或强制正文行高；只在原有明确设置的组件上保留行高，勿凭网页排版惯例增加全局值。

## Layout

Shell 默认窗口为 1220 × 800，最小为 1100 × 680；标题栏高 40，展开侧栏宽 216。普通入口的标题栏保留最小化、最大化、关闭和侧栏开关；快捷键入口隐藏整条自绘标题栏，以右侧 40 DIP 窄区提供关闭、侧栏开关及拖动／双击最大化，不占用整行顶部空间。内容区域外边距为 `8,8,16,16`，外层白色工作区圆角为 12，并以不透明主题背景保证现有文字呈现路径。

侧栏按工具入口、快捷操作、应用管理分区：屏幕翻译和截图工具为内容页，剪贴板与资料库入口打开独立窗口，使用外开图标和提示说明；外开工具不改变内容页选中态。主入口使用本地 `RailNavigation`／`RailEntry`，高 42 DIP；底部导航高 36 DIP。快捷操作使用单行名称和右侧快捷键，允许省略超长组合，通过鼠标悬停或键盘聚焦查看完整提示。四个主要入口及快捷操作在最小窗口下无需滚动。底部保留设备与同步、设置、关于与更新；发现新版时在关于行右侧显示紧凑安装按钮，不额外占用一行。

屏幕翻译顶部为模块标题及截图翻译／持续翻译两个模式，截图工具、设置及关于页隐藏这一区域。截图翻译内容使用垂直滚动、禁止横向滚动，页边距为 `36,16,36,28`。三列原文语言、目标语言、翻译服务位于任务区上方，控件高 40；任务区下方是状态与 Esc 提示及截图选项。截图工具页单独呈现截图主动作、贴图与取色两个辅助动作及操作说明，小窗口允许正文纵向滚动。模块导航以 [2026-11-09 导航记录](docs/Multitool-Navigation-2026-11-09.md) 为准，旧 `.impeccable/surface.md` 中单一翻译导航与应用配置描述属于此前设计阶段。

设置页面保留现有滚动、分组与服务内容；OCR 设置内容最大宽 760，动作区使用 `WrapPanel`。当前是桌面最小窗口加滚动策略，没有实现移动端断点或窄屏全局重排。

## Elevation & Depth

主工作区以底色、圆角和间距建立层次，不绘制侧栏与工作区之间的分隔线；首页任务区及导航默认没有投影。不把所有内容装进悬浮卡片，也不宣称整个应用完全无阴影：现有下拉弹层保留 `DropShadowEffect`（BlurRadius 12、ShadowDepth 2、Opacity 0.15、黑色），以及 `PopupAnimation="Slide"`。其他既有辅助窗口和组件可保留原有局部效果。

只保留已有导航与侧栏动画，不新增入场或装饰动画。

## Shapes

常规输入与按钮采用 control 圆角；列表项采用 list-item，导航采用 navigation，首页橙黄区域采用 capture-panel，内容宿主采用 content-host。输入、次按钮和必要弹层边界通常厚 1，输入聚焦边界为 1.5；静态设置分组和持续翻译分组不描边。`ProductFocus` 使用 AppAccent 的 2 厚描边、8 圆角与 -3 外边距。

首页签名是不可点击的静态框选四角：132 × 104，线宽 3，内置 Aa → 译 的小面板。它表达原位翻译，不作为真实预览、拖拽区域或额外功能。窗口标题栏按钮保留原生习惯的几何形式，不跟随内容按钮统一圆角。

## Components

### Buttons

`ProductButton` 基于 `AccentButton`，使用主要动作颜色和对应 hover/pressed 资源；标准高、内边距见 frontmatter，首页开始框选采用 capture-action 覆盖。禁用透明度为 0.45。

`ProductSecondaryButton` 基于 `FlatButton`，白底/主题底色、细描边，hover/pressed 使用 `AppButtonHoverBg` / `AppButtonPressedBg`。`ProductLink` 继承该模板，默认透明、无边框、左对齐、左右内边距 10；模板仍提供悬停反馈。三者使用 `ProductFocus`。

### Inputs / Fields

`ModernTextBox` 与 `ModernComboBox` 默认高见 input；首页与 OCR 页按实际布局覆盖到 40。TextBox 左右内边距为 9，聚焦使用 `AppInputFocusBorder`，禁用透明度为 0.5。保留 ComboBox 搜索、选择事件、密码字段原生语义及 `PART_ContentHost` 等模板部件，不能为造型移除。

资料库搜索区沿用窗口底色，不再铺满白色背景；搜索标签为 14、SemiBold、主文字色，输入文字为 14、输入框高 36。两页保持一致，详情白色分区不受影响。

### Navigation / Lists

`ProductNav` 是同组 RadioButton：未选中采用次级文字色和透明底；hover 为按钮悬停底；选中为 AppAccent/AppAccentFg 加 SemiBold。列表 `ProductListItem` 内边距 16，条目下间距 6；普通项使用 AppCardBg 和 AppBorder，hover 使用 AppListHoverBg，选中使用 AppListSelectedBg、AppAccent 描边及短侧标记，选中态优先于悬停。资料库和剪贴板复用此模板，以 AppListBg 区分列表槽与条目；资料库详情采用白色独立分区，保留原有左右布局。剪贴板搜索使用输入底色及聚焦描边，分类选中使用 AppAccent/AppAccentFg；资料库和设置 Tab 使用 AppListSelectedBg。不可把列表选中和导航选中误当成同一种填色。

### Task Surface / Hints

橙黄框选区域只保留一个明显的“开始框选”动作，并显示真实当前快捷键；右侧几何不参与命中测试。快捷键底板使用 AppSurfaceBg。Esc 提示使用暖色背景与暖色文字，状态及辅助文案使用 ProductHint。隐私说明保持可见，不用纯装饰图替代操作提示。

### Settings Containers

保留现有 `CardBorder`、服务分组与独立 OCR 配置，不把“保存识别设置”误写成自动保存，也不把其他设置的自动保存改成显式保存。视觉修改沿用主题资源，不改配置字段、认证、请求或业务逻辑。

## Do's and Don'ts

### Do:

- **Do** 优先复用 SharedStyles/ProductStyles 与主题资源键，新增页面在 Light 和 Dark 下同时核对。
- **Do** 保留键盘焦点、禁用反馈、可访问名称、现有命名控件和事件。
- **Do** 以真实 XAML 和渲染结果核对尺寸、换行及层次，区分静态展示与功能验收。

### Don't:

- **Don't** 恢复绿色主视觉、把浅色主背景替换成灰色，或把整屏铺满品牌色。
- **Don't** 强制覆盖用户已保存的 Dark 选择，或给深色浅蓝按钮硬配白字。
- **Don't** 因视觉改造删减功能、隐私说明、快捷键或服务专属配置。
- **Don't** 把 HTML/CSS 检测器结论或离屏渲染当作本机真实窗口、全局热键、真实 Provider、抓屏覆盖层、多显示器和已安装版本验收。

提取来源：`PRODUCT.md`、`.impeccable/surface.md`、`src/OverTranslate/Themes/{LightTheme,DarkTheme,SharedStyles,ProductStyles}.xaml`、Shell/Home 与 Settings/OCR 视图、`LocalizationService`、`ThemeService` 和 `AppSettings`。本轮可用图像证据位于 `.impeccable/review`：desktop.png / desktop-dark.png 为 1220 × 760，minimum.png 为 1100 × 680，均是实际 WPF 控件树离屏渲染，不是桌面截图。原生 XAML 未运行 HTML/CSS detector；本文不新增任何真实服务或桌面交互验证结论。


## 2026-11-10 辅助窗口与弹框一致性

- 设备总览、配对与同步、文件发送、文件接收、同步记录使用 `ProductToolWindow` 和 `ToolWindowHeader`，复用主窗口 40 DIP 标题栏、系统窗口操作与 `WindowFrame`；不可调整大小的接收确认只显示关闭按钮。资料库管理入口和独立 OCR 设置由并行任务接入同一组件，资料库快捷模式保留自身的紧凑窗口行为。
- 弹框标题使用 `ProductDialogTitle`（22 DIP / SemiBold），四个传输及记录弹框内容边距为 24 DIP。发送窗口最小为 440 × 480，记录窗口为 460 × 440，设备总览为 520 × 440；保证固定操作区与可滚动列表共存。
- 同步复选框和通用设置复用 `ModernCheckBox`；接收设备复用 `ModernComboBox`；记录、设备和待发送项目复用 `ProductListItem`，选中项使用 `AppAccentSubtle`。长文件名和设备名称换行，接收确认限制高度并滚动正文，底部按钮保持可见。
- 辅助窗口的标题栏按钮不参与正文 Tab 顺序。四个传输／记录列表使用像素滚动，超高条目末尾应可达，同时保留虚拟化；不能以关闭内容滚动的方式绕过超高条目问题。
- `FlatButton`、`AccentButton`、`ModernCheckBox` 和 `OverlayCloseButton` 统一使用现有 `ProductFocus`；复选框补齐禁用反馈。错误说明使用 `ProductInlineError`，无错误时不占位，错误出现时使用语义错误色。
- 本轮执行五个窗口的常规尺寸及最小尺寸浅深主题 WPF 离屏渲染；实际产品仍为浅色入口。范围和验证边界见 [审查记录](docs/Frontend-Consistency-2026-11-10.md)。

## 2026-11-09 设置归类与 Tab 统一

- 设置保留四个同级 Tab：文字识别、AI 与翻译、快捷键、通用与诊断，首次进入默认展示 AI 与翻译。设置和资料库共用 `ProductTab` 圆角底色选中样式，选中颜色与字重仅作用于标题，不传给正文。
- 截图首页的“截图选项”管理自动翻译和原位覆盖；OCR 页管理引擎、提取格式与竖排/漫画；翻译服务页管理备用策略。六个快捷键集中展示，原“随时调用”设置页取消。
- 资料库保留独立窗口及两个内容 Tab。快捷键唤出时隐藏大标题、快捷键提示及顶部设置区，同时移除系统标题栏并保留紧凑关闭、拖动及缩放；侧栏／托盘打开时恢复完整管理区。AI 整理入口跳转到总设置，快捷键入口保留，不清空当前内容状态。设备与同步位于主窗口左下角，继续复用原配对窗口。
- 设置表单使用 `0,24,12,24` 内边距与最大宽度 760 DIP。普通行为选项即时保存；OCR 连接配置显式保存，支持取消服务更改；保存失败保留输入并回滚未生效的 OCR 配置。
- AI 与翻译页集中展示 AI 翻译、提示词 AI 整理、DeepL 和备用策略。AI 整理的地址、模型、Key 与自动整理开关在页内显式保存，复用资料库原有存储和校验；切换页面保留草稿。主窗口标题栏常显“设置”，快捷唤出模式右侧保留设置齿轮入口。
- 本轮验证为自动回归与 96 DPI WPF 离屏渲染，不代表不同显示器 DPI 或真实服务联调。


## 2026-11-11 Fluent 分层调整

以 Microsoft Fluent 的窗口底层和内容层为参考，在既有 WPF 字典中落地，不迁移 UI 框架，也不模拟 Acrylic/Mica 透明材质。分析、来源、实施范围和验证见 [视觉语言调整](docs/Desktop-Visual-Language-2026-11-11.md)。

- Shell 的本地 `RailNavigation` 使用淡蓝底、蓝字和 3 DIP 选中标记；`TranslationMode` 与设置/资料库 `ProductTab` 使用圆角选中底，不再用横贯页面的线条组织导航。`ProductNav` 仍保留原有通用模板，Shell 以本地模板为准。
- `CardBorder` 使用浅色分组底，取消描边，圆角 10，内边距 20,16；`InnerGroupBorder` 与 `ToggleRowBorder` 使用白色内容底且不描边。持续翻译的 `ProductRealtimeSection` 使用分组底、圆角 10、内边距 20。
- OCR、AI 与翻译、网络代理用浅色底和 20 DIP 留白组成设置组；原有保存、取消、测试与凭据控件保持原契约。
- 资料库列表取消逐行分割线，以悬停、选中底和 3 DIP 标记保持可操作性；只读预览使用浅色底。
- 消息弹窗分为白色正文与浅灰操作区，正文 14 DIP / 行高 24；发送文件、设备、同步记录使用白色正文与浅灰标题区；表单输入边界、键盘焦点、危险操作确认与 Esc 取消继续保留。
- 本轮验证使用独立 VisualRefresh 构建与合成测试资料；离屏渲染不能证明真实显示器观感或在线服务可用性。
