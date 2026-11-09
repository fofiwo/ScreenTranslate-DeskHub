# 来源与分支边界

- 底座：[OverTranslate](https://github.com/Hon-Lu/OverTranslate)，提交 `5365f850a03a62d81b530d4211eabb8516f53871`，MIT。原版权和第三方声明保留；本分支使用独立名称、配置目录和实例标识。
- 设计参考：[WindowTranslator](https://github.com/Freeesia/WindowTranslator)，参考提交 `7fd2ac26adad55992892633c853e0cafef2ae630` 的 `UserSettings.cs`、`WindowMonitor.cs` 与 OCR 接口。借鉴按应用配置、客户区相对区域和可选附着思路；实现写在本分支中，没有直接合并其客户端或 NuGet 插件系统。
- 百度接口依据：[通用文字识别（含位置）](https://cloud.baidu.com/doc/OCR/s/vk3h7y58v)。实际可用性、收费及配额由百度账号和当期产品政策决定。
- 提示词与模型资料功能参考 [FloatFolder](https://github.com/fofiwo/FloatFolder) 1.0.24；移植平台元数据、目录协议选择和提示词整理行为至 C# / WPF，未引入 Electron 运行时。Copyright (c) 2026 FloatFolder，MIT，完整许可保存在 [FloatFolder-MIT.txt](docs/licenses/FloatFolder-MIT.txt)。

软件图标沿用上游资源；不是全新品牌设计。模型和原生运行库的许可仍按上游 `THIRD-PARTY-NOTICES.txt` 与对应模型目录说明执行。
