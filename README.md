# ScreenTranslate DeskHub

Windows 桌面工具集：截图 OCR 翻译、剪贴板历史、提示词库与模型库，以及局域网双向同步。

[下载最新安装包](https://github.com/fofiwo/ScreenTranslate-DeskHub/releases/latest) · [安装和同步说明](docs/ScreenTranslate-Install.md)

支持 Windows 10/11 x64。运行 `ScreenTranslate-win-Setup.exe` 安装。安装版将个人数据独立保存在 `%LocalAppData%/ScreenTranslate-data`，升级保留该目录。发布内容不包含开发电脑的剪贴板、资料库、API Key 或用户设置。

软件启动后和运行期间每 30 分钟检查新版本。发现新版时，主窗口左下角显示版本和安装入口；也可从“关于”或托盘主动检查。点击更新窗口中的“立即更新”后下载、校验、安装并重启。下载失败可以重试，更新不要求手动替换程序文件。早期未启用在线更新的本地版需先手动安装一次本仓库的安装版。

首次启动可选择同步剪贴板、提示词库和模型库；两台电脑首次需通过地址、配对码及设备确认完成配对。模型同步包含 API Key，仅应与自己的可信设备配对。当前退出或断线后需重新配对；剪贴板同步在线期间新复制的文本/图片，不补传离线历史。资料库首次合并并持续同步，保留修改冲突副本。

## 开发与发布

使用 .NET 8 SDK、Windows x64 开发环境：

```powershell
dotnet test src/OverTranslate.Tests --filter DeskHubUpdateTests
dotnet tool install vpk --version 1.2.0 --tool-path .tools/velopack
./build-screen-translate-installer.ps1 -Version 0.3.0
```

向本仓库 `main` 推送源代码后，GitHub Actions 自动测试、构建干净安装包，发布 `0.3.<run_number + 1>` 稳定版及 `releases.win.json` 更新源（跳过已用于本机安装的 0.3.6）。测试或构建失败时不发布新版；完整资产上传完成后才公开版本。发布凭证使用 Actions 内置权限，客户端读取公开更新源，不需要 GitHub Token。

正式版直接读取 Release 的静态更新清单，避免 GitHub 匿名 API 限流影响检测。下载锁定提示中的版本，即使等待期间发布了下一版，也能获取原版本的更新包。

本机原开发目录通过 `./publish-deskhub.ps1 -Message '本次更新说明'` 导出清理后的源码并推送独立公开仓库；脚本不提交原工作区的 Git 历史及用户数据。普通公开仓库克隆直接向 `main` 推送即可触发发布，无需再次导出。

本仓库只包含清理后的源码快照及后续提交，不包含原工作区的历史、个人数据或本机 Agent 配置。源码和安装包保留上游版权声明。安装包目前未作 Windows 代码签名，可能显示未知发布者或 SmartScreen 提示。

## 开源许可

基于 [Hon-Lu/OverTranslate](https://github.com/Hon-Lu/OverTranslate)，遵循 MIT。原作者版权及组件许可证见 [LICENSE](LICENSE)、[FORK-NOTICES.md](FORK-NOTICES.md)、[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) 和 `docs/licenses/`。
