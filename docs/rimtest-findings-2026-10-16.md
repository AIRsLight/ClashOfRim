# RimTest 参数与跨语言回归发现（2026-10-16）

## 已修复：非 ASCII 语言名称阻断 HTTP / WebSocket

双客户端 `coverage50-french` 的法语客户端在版本检查阶段失败，消息为 `HttpRequestException: Request headers must contain only ASCII characters.`。`French (Français)` 等游戏语言目录名直接进入 `X-ClashOfRim-Language` 请求头；中文、日文等名称也有同样风险。

HTTP 与 WebSocket 现在共用 `ServerLocalization.EncodeLanguageHeader`：规范化语言后进行 URI 百分号编码；服务端在请求本地化边界解码。游戏世界内的语言字段和显示名称不改写。既有 ASCII 请求头仍可读取。

验证：`Tools/ClashOfRim.Client.Core.Tests/LanguageHeaderTests.cs` 覆盖英文别名、中文、法语、自定义/不支持的语言、控制字符和旧 ASCII 请求头，客户端核心套件通过。修复后的法语实机请求已越过版本检查并到达产品原有的语言差异确认窗口；完整跨语言流程的结果另见矩阵报告，不能把这一局部结果视为全流程通过。

原始失败证据：`G:/RimTestRuns/ClashMatrix/20261016-083925-088d0945/coverage50-french/20261016-083931-0ee99c02`。后续运行不会覆盖这一目录。

## 测试适配器修正及判定标准

- 世界参数页使用 `rainfall`、`temperature`、`population` 字段；生成后的 WorldInfo 才使用 `overall*` 字段。首次测试的适配器字段错误保留为失败。
- 游戏会把已有线程的区域格式设为 en-US。区域格式场景必须显式在 RimTest 执行线程应用测试配置，并通过 `runtime.hello` 校验实际生效值。不能只依据启动参数宣称覆盖 fr-FR / de-DE / tr-TR。
- 1.7 表面地块存档对海拔、温度、降雨、湿度、污染进行 ushort/byte 量化。新生成世界与已装载世界不能直接比较浮点位模式。摘要按实际存档可表示精度比较，另保留原始地块值和首个不同块的诊断；几何坐标仍逐值精确比较。
- 当前游戏难度字段名为 `allowInstantKillChance`；测试不继续使用旧记忆中的 `colonistInstantKillChance`。
- 混合语言场景只选择产品原本允许的“仅语言警告继续”按钮；协议、模组内容或配置不兼容仍失败。该测试不移除产品的语言风险提示。

本地旧反编译资料用于定位适配器名称，实机运行负责核验。REA 元数据调用 `ev_3c099faa24502ceb67ccdadf348326ad1802cba980cdd23743796de42974d550` 因 64 MiB 表示预算返回 unavailable，不能作为当前程序集完整成员覆盖的证据。没有提交游戏程序集或反编译源码。
