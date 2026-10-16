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

同一五 DLC 运行的 `snapshot-save-reload` 失败。B 客户端使用英文显示、显式 fr-FR 区域格式，重载前表面层 119904、轨道层 488 个地块，重载后变成 590492 / 2432；覆盖率从 0.3 变成 300000000，污染从 0.25 变成 25，几何摘要同时改变。这超过浮点量化误差，不能放宽断言。需区分 RimWorld 原生文化格式存档行为和模组重载补丁；尚未作归因结论。最终目录将混合语言 DLC 业务场景的 B 区域格式设为 en-US，并保留独立 `dlc-culture-stress` 场景复现 fr-FR，以免把文化格式夹具和多语言问题混为一谈。

## 已修复：重复借款入口进入强制恢复

`G:/RimTestRuns/ExpandedCore` 中 `rimtest-core-complete.log` 对应运行，在已有未还贷款时再次调用原借款入口：服务端正确拒绝，客户端 `localAtomicMutationPending=true` 并停在 `SnapshotConfirmationFailureWindow`。它不是重复请求成功。测试要求拒绝不改变库存/快照；客户端入口现增加对当前活动贷款状态的前置拒绝，复用原状态格式化逻辑，`G:/RimTestRuns/ExpandedCore/20261016-094239-94c7f69e` 的非法输入、借款/重复借款/还款/重复还款用例通过；`LocalOnly/rimtest-bank-client-tests.log` 客户端回归通过。该运行在后续测试的交易刷新等待处失败，不计作整场通过。

## 测试适配器修正及判定标准

- 世界参数页使用 `rainfall`、`temperature`、`population` 字段；生成后的 WorldInfo 才使用 `overall*` 字段。首次测试的适配器字段错误保留为失败。
- 游戏会把已有线程的区域格式设为 en-US。区域格式场景必须显式在 RimTest 执行线程应用测试配置，并通过 `runtime.hello` 校验实际生效值。不能只依据启动参数宣称覆盖 fr-FR / de-DE / tr-TR。
- 1.7 表面地块存档对海拔、温度、降雨、湿度、污染进行 ushort/byte 量化。新生成世界与已装载世界不能直接比较浮点位模式。摘要按实际存档可表示精度比较，另保留原始地块值和首个不同块的诊断；几何坐标仍逐值精确比较。
- 当前游戏难度字段名为 `allowInstantKillChance`；测试不继续使用旧记忆中的 `colonistInstantKillChance`。
- 混合语言场景只选择产品原本允许的“仅语言警告继续”按钮；协议、模组内容或配置不兼容仍失败。该测试不移除产品的语言风险提示。

本地旧反编译资料用于定位适配器名称，实机运行负责核验。REA 元数据调用 `ev_3c099faa24502ceb67ccdadf348326ad1802cba980cdd23743796de42974d550` 因 64 MiB 表示预算返回 unavailable，不能作为当前程序集完整成员覆盖的证据。没有提交游戏程序集或反编译源码。

## 待修复：重复接受已确认礼物产生第二份物资

`LocalOnly/rimtest-core-complete6.log` 对应的 `G:/RimTestRuns/ExpandedCore` 运行中，B 接受 A 的 5 木材礼物后库存 250 → 255；待快照事务完成，再调用同一个 `HandleEventLetterAction(eventId, Accept)` 并刷新事件、推进 600 tick，库存变成 260。服务端已确认并不保证旧客户端操作入口不会再次应用本地载荷。当前入口只确认事件详情存在和业务类型，没有复用队列/处理状态限制。

回归已保留在默认矩阵独立 `gift-repeat-accept` 场景中，依然要求第二次库存不变。单独运行是为了不让已污染的库存干扰后续其他功能验收，不是忽略失败。后续应统一事件动作可用性与本地已确认状态，避免只给礼物加一个孤立缓存。

独立场景再次复现：`G:/RimTestRuns/FinalFeatures/20261016-100813-60ed93cd/gift-repeat-accept/20261016-101249-993141d2`。首次交付后库存 305，重复接受后 310；联盟、原远行队装载和首次礼物接受均先通过。这一证据不依赖其他经济业务留下的库存。

## 待定位：跨语言地区文字初始化与区域格式解析

- `WorldMatrix/20261016-093608-6e0fa7f3` 的中文 100% 场景有地区数量断言失败，日志为 `WorldFeatureTextMesh_Legacy.Init` 空引用；后续 `FinalMatrix/20261016-094519-d7329d82/coverage50-french` 在 `RefreshWorldFeatureTexts` / `GenerateWorldFromServerConfiguration` 的完成回调复现相同异常，阻断选址页。最终报告应区分 Headless 与无窗口渲染对照，不能凭无渲染测试断言所有正常桌面也失败。
- 显式 fr-FR / de-DE 数字格式的 `hot-custom-comma` 中，1.37 威胁系数被应用成 137，0.17 / 0.63 也被放大；需检查 XML 写读的文化格式契约。该测试环境会主动覆盖游戏默认 en-US，所以不能直接声称普通法语游戏必然如此。
- 非 Headless 的 fr-FR 对照 `RenderControl/20261016-095211-bf945fd3` 被原生 `Verse.Grammar.Rule_String` 解析 `0.15` 的 `FormatException` 阻断。它说明强制区域格式也影响基础游戏规则解析，尚不能用这次运行排除或确认渲染模式与地区文字空引用的关系。
- 最低温度 / Extreme 的一次运行在错误门禁发现 `{GROUP}` / `{COUNT}` 未替换；保留原始错误，不把随机出现的本地化错误加入白名单。

非 Headless、法文显示但保留 en-US 数字格式的对照 `G:/RimTestRuns/RenderControl/20261016-095845-b438bc1b` 已通过建档、运行中难度修改及存档重载，仍发现地区数量不一致。因此地区数据断言失败不能全部归于 Headless。该对照同时暴露了观察器错误地比较玩家代理派系、Core 隐藏意识形态的问题，现已按上面的比较契约修正；旧报告保留。

## 测试框架生命周期修正

管理员踢人/重连用例暴露 `game.state` 在半初始化游戏上读取 `TickManager.Paused` 的空引用。该属性会间接读取尚不存在的世界 UI / tilePicker；仅检查 Playing 不足以覆盖加载中间态。框架现在统一从已存在世界 UI 的 Playing 游戏获取当前时钟；菜单/装载期间返回空暂停状态，旧 tick 操作的统计仍保留，但不再借用新游戏的暂停组件。`time.status`、`time.context` 复用同一判定，不捕获并吞掉异常。

套件还修正了三类前置条件错误：登录必须等待新的游戏 epoch；恢复的初始快照可能仍有开局运输舱，统一等待落地后才检查经济库存；退出必须走原版返回菜单流程，仅调用登出钩子会留下可自动重连的游戏组件。这些中间失败保留在原报告中，不记作已确认的模组缺陷。框架独立 smoke 已验证返回菜单以及随后三个生命周期接口，证据为 `G:/RimTestRuns/FrameworkMenu/20261016-102619-1f7f4f98/report.json`。

## 待修复：真实返回菜单后残留登录/上传忙碌状态

`G:/RimTestRuns/VerifiedCore/20261016-102544-2beecfa7` 中，踢人重连和封禁/解封通过。随后上传并等待空闲，经原版 `GenScene.GoToMainMenu` 退出后，`game.state` 已是 `hasGame=false`、`playing=false`、菜单就绪；但客户端 `manualSyncInProgress=true`、`snapshotUploadInProgress=true`，状态停在 “Logging in to the server / Preparing and uploading the current save”。下一次登录入口前的空闲等待超时，因此错密码与维护登录的后续步骤被阻断。

这是走真实退出流程后的失败，不是前述“只调用退出钩子却仍保留游戏”的夹具问题。需要检查退场期间已排队的登录/上传回调、会话代际取消和忙碌标志的清理。现有证据尚不足以锁定具体回调；不应简单添加超时清标志来使测试通过。此前只在仍加载的游戏内调用退出钩子的通过记录，不能证明真实退出到菜单的重连正常。

独立的 100% 覆盖率/外部叙事者场景也复现同样忙碌状态：`G:/RimTestRuns/FinalMenuWorld/20261016-102625-c3e63048/mod-storyteller100/20261016-102625-0bcaceed`。它已通过前面的 12 项世界/难度/存档检查，在真实退出后的 `session-reconnect` 失败，因此不依赖管理员封禁、经济或袭击业务。

## 修复进度（2026-10-16）

### 重复礼物交付

- 实际处理入口与信件按钮复用同一事件组/操作资格判断；手动礼物入口同时拒绝同步中和等待确认的事件。
- 共用单事件确认流程在成功后移除缓存中的可操作组，旧信件或缓存详情不能再触发资源落地。
- RimTest `RepairGift/20261016-110433-8deb20ff/gift-repeat-accept/20261016-110448-3fad5af4`：`caravan-gift-accept`、`gift-repeat-accept`、`snapshot-save-reload` 通过。该轮未包含退出修复，完整场景仍需单独验证重连。

### 退出后的旧游戏自动重连

- 登出清理复用主菜单进入流程的源游戏隔离，保留对正在异步卸载的旧 `Game` 的自动登录阻断。新游戏实例和显式登录仍按原流程释放阻断。
- `RepairExit/20261016-110641-213521b1/core-features/20261016-110652-5a76f023`：41 个功能断言通过，包括踢出/封禁重连、维护锁、完整经济/社交/突袭业务、存读档和真正返回菜单后的重连。
- 最后的日志门禁发现新的 `Gene_14/Gene_15` 重复 load ID，整场仍记失败；不能把功能断言通过当作无错误验收。另行定位角色交换后的嵌套对象 ID。

### 区域格式下的数值膨胀

- Scribe 读取数字使用固定格式，但写入使用当前区域格式；在完整游戏序列化与独立 XML 包两个共用边界设置 invariant culture，结束（含异常）后恢复调用方格式，保留 UI 语言。
- 原版 `Rule_String` 同样使用当前格式解析 Def 中的固定小数权重，法国/土耳其区域夹具会在世界生成前报错；纳入同一数值数据边界处理，没有关闭区域夹具。
- 客户端回归覆盖 en-US/fr-FR/de-DE/tr-TR/zh-CN 的 0.3、0.25、1.37、语法权重及异常后的格式恢复。
- `RepairWorld/20261016-111436-4e6bf8ab/hot-custom-comma/20261016-111724-71aa228f`：100% 世界、自定义难度 1.37/0.17/0.63、运行时修改、存读档、退出重连和错误日志门禁全部通过。

### 多语言地名初始化与首个地名丢失

- 1.7 的 Legacy TextMesh 初始化访问不存在的渲染器。世界地名统一使用游戏已有的 TMP/引擎字体后端，保留地名、位置、大小和游戏原有刷新流程。
- 底图加载遵循 Scribe 默认值省略规则：缺失 `uniqueID` 对应 0，不再丢弃第一个地名（已复现 Island ID 0）。
- `RepairWorld/20261016-111436-4e6bf8ab` 中 `coverage50-french/20261016-111444-3715b754` 与 `coverage100-chinese-host/20261016-111551-1b5876a0` 全场通过，包含所有地名 ID/位置/大小及错误门禁。
- 当前程序集 SHA-256 `eff7d97949f919fb2b151ebfb906e9be749b63410b7dbda78eeebb7972b245c3` 的局部 IL 核对确认旧 TextMesh 分支及材料访问；REA 元数据预算受限，不计作完整反编译覆盖。无头回归证明状态与无异常，字体像素显示仍需带渲染验证。

### 并发本地化格式化串值

- 并发负向测试复现原版命名参数/格式化器静态缓存竞争：8 个线程返回其他线程的 GROUP/COUNT，甚至数组越界。
- 对共用格式化入口和参数收集入口统一同步，覆盖模组网络线程和原版 UI 调用；支持重入，异常路径释放锁，未复制游戏格式化算法。
- 客户端 8×1,000 次混合入口回归通过；`RepairFinal/20261016-112058-170cea6d/core-features/20261016-112106-581703f3` 完整 42 项通过（包括错误门禁）。

### 扩展回归：Core 基因 ID 重复

- 1.7 的 Core 角色也有外观基因，原实现只在 Biotech 激活时重写 Gene ID，远端地图/支援角色混入本地世界后触发重复 load ID。
- 基因身份迁移改为共用 Core 能力，远端 XML 重写与角色交换始终注册；Biotech 仍负责其专有缓存，复用同一个 ID 分配循环。
- 上述 `RepairFinal` Core 全场 42/42，通过远端观察、支援、突袭、存读档后的错误日志检查，未忽略原 `Gene_14/Gene_15` 报错。
