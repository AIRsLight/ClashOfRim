# RimTest 参数与跨语言回归发现（2026-10-16）

## 已修复：非 ASCII 语言名称阻断 HTTP / WebSocket

双客户端 `coverage50-french` 的法语客户端在版本检查阶段失败，消息为 `HttpRequestException: Request headers must contain only ASCII characters.`。`French (Français)` 等游戏语言目录名直接进入 `X-ClashOfRim-Language` 请求头；中文、日文等名称也有同样风险。

HTTP 与 WebSocket 现在共用 `ServerLocalization.EncodeLanguageHeader`：规范化语言后进行 URI 百分号编码；服务端在请求本地化边界解码。游戏世界内的语言字段和显示名称不改写。既有 ASCII 请求头仍可读取。

验证：`Tools/ClashOfRim.Client.Core.Tests/LanguageHeaderTests.cs` 覆盖英文别名、中文、法语、自定义/不支持的语言、控制字符和旧 ASCII 请求头，客户端核心套件通过。修复后的法语实机请求已越过版本检查并到达产品原有的语言差异确认窗口；完整跨语言流程的结果另见矩阵报告，不能把这一局部结果视为全流程通过。

原始失败证据：`G:/RimTestRuns/ClashMatrix/20261016-083925-088d0945/coverage50-french/20261016-083931-0ee99c02`。后续运行不会覆盖这一目录。

## 已修复：世界就绪前通知导致运行中难度更新丢失

`coverage100-chinese-host/20261016-085749-dd18ad11`（父目录 `G:/RimTestRuns/ClashMatrix/20261016-084848-75c2e476`）中，管理员通过原游戏编辑页从 Randy/Medium 切到 Phoebe/Custom，服务端已保存新值，在线客端 90 秒内仍保留旧难度。客端世界目录状态为“服务器未返回世界配置”。

提交配置会清空旧底图，准备等待新底图上传；原实现此时就通知其他客户端，但底图成功上传后没有通知。现在通知在上传成功后发送，避免读者拿到 `WorldConfigured=false` 后错过更新。没有增加客户端重试轮询。`20261016-090944-4751d9ec` 下 Core 与五 DLC 混合语言场景的 `runtime-difficulty-sync` 均通过，包含自定义字段逐值比对和恢复原设置。`LocalOnly/rimtest-expanded-regression.log` 的完整回归（含客户端）通过。

## 待修复/继续定位：文化目录重建缺少显示元数据

五 DLC 场景 `G:/RimTestRuns/ClashMatrix/20261016-090944-4751d9ec/dlc-cultures/20261016-091241-c4be466b` 的 `world-persistence` 失败：从同一数据库构建新服务容器后，`rimworld.ideology.world-ideo-catalog` 的 `cultureLabel`、`cultureIconPath`、`factionDefName`、`iconPath`、`colorHex` 等由原值变为 null。canonical culture/意识形态字段存在，不应描述为整个文化丢失。需核对重建索引/投影时的元数据合并；当前测试保留失败，没有忽略这些字段。

## 待定位：fr-FR 夹具下存档重载改变世界网格

同一五 DLC 运行的 `snapshot-save-reload` 失败。B 客户端使用英文显示、显式 fr-FR 区域格式，重载前表面层 119904、轨道层 488 个地块，重载后变成 590492 / 2432；几何摘要同时改变。这超过浮点量化误差，不能放宽断言。需区分 RimWorld 原生文化格式存档行为和模组重载补丁；尚未作归因结论。默认游戏区域格式的对照与后续矩阵结果需另看实际报告。

## 已复现：重复借款入口进入强制恢复

`G:/RimTestRuns/ExpandedCore` 中 `rimtest-core-complete.log` 对应运行，在已有未还贷款时再次调用原借款入口：服务端正确拒绝，客户端 `localAtomicMutationPending=true` 并停在 `SnapshotConfirmationFailureWindow`。它不是重复请求成功。测试要求拒绝不改变库存/快照；客户端入口现增加对当前活动贷款状态的前置拒绝，复用原状态格式化逻辑，完整复验结果待矩阵记录。

## 测试适配器修正及判定标准

- 世界参数页使用 `rainfall`、`temperature`、`population` 字段；生成后的 WorldInfo 才使用 `overall*` 字段。首次测试的适配器字段错误保留为失败。
- 游戏会把已有线程的区域格式设为 en-US。区域格式场景必须显式在 RimTest 执行线程应用测试配置，并通过 `runtime.hello` 校验实际生效值。不能只依据启动参数宣称覆盖 fr-FR / de-DE / tr-TR。
- 1.7 表面地块存档对海拔、温度、降雨、湿度、污染进行 ushort/byte 量化。新生成世界与已装载世界不能直接比较浮点位模式。摘要按实际存档可表示精度比较，另保留原始地块值和首个不同块的诊断；几何坐标仍逐值精确比较。
- 当前游戏难度字段名为 `allowInstantKillChance`；测试不继续使用旧记忆中的 `colonistInstantKillChance`。
- 混合语言场景只选择产品原本允许的“仅语言警告继续”按钮；协议、模组内容或配置不兼容仍失败。该测试不移除产品的语言风险提示。

本地旧反编译资料用于定位适配器名称，实机运行负责核验。REA 元数据调用 `ev_3c099faa24502ceb67ccdadf348326ad1802cba980cdd23743796de42974d550` 因 64 MiB 表示预算返回 unavailable，不能作为当前程序集完整成员覆盖的证据。没有提交游戏程序集或反编译源码。
