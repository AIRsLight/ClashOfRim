# 模块拆分最终验收：进行中

本轮代码基线为 `0f1affba` 加工作区，承接[主计划](partial-module-audit-2026-10-17.md)的 A1–A9、M1–M12 和六条每步验收标准。**partial 清理已经完成，整体目标尚未完成验收。** 本表分别记录已经核对的范围和仍需补足的证据，不把编译或结构扫描等同于游戏行为验证。

**最新状态（M13c2）：A1／A2／A4 的状态所有权、组合根和兼容例外已完成核对；修复了仍留在旧门面的 WebSocket 准入规则。剩余 A5 页面生命周期、A7 DLC 能力与加载生命周期两类最终验收。** 最近扫描的 1,914 个 C# 文件中，partial 类型／声明／文件为 0／0／0；本步没有新增 partial。以下保留各审计时点的数量与证据，不代表全量游戏行为已经回归。

## 最新复审：还剩多少 partial 耦合

复审基线为 `123056d9` 加当前工作区。重新运行 Roslyn 清单工具，覆盖 `Source`、`Tools`、`ThirdPartyCompat/Source` 的 **1,912 个 C# 文件**，排除 `bin`／`obj`；另对整个 ThirdPartyCompat 进行文本交叉检查。结果如下，冻结 1.6 和独立 RimTest 子仓库不计入本次迁移范围。

| 计数口径 | 当前剩余 |
| --- | ---: |
| 跨文件 partial 类型／耦合组 | **0** |
| 单文件多余 partial 类型 | **0** |
| partial 类型声明／涉及文件 | **0／0** |

最后两组兼容 API、银行注册表分别已在 `4aae8aff`、`1183cf28` 归并；五处单声明修饰符已在 `0f1affba` 清理。旧报告中的 **7／13／11** 是 M12c8 时点的历史数量，不是当前待办。当前机器结果为 `LocalOnly/partial-module-audit/inventory-current-reaudit.json`（空清单）。

本次同时重跑两组边界单元，均退出 0：客户端检查 1,702 个纯模块／生成类型，另覆盖兼容能力 127、Ideology 装配 14、页面 213、窗口宿主 9、事件适配 318 个类型；服务端检查 466 个模块类型、81 个端点及恢复归属。客户端和服务端业务模块目录中未检索到对 Mod、全量 NetworkState、服务器宿主或 IServiceProvider 的直接引用。上述结果证明现有守卫范围内的边界，不足以证明所有跨域生命周期都已验收。

### 复审时的四类验收及后续状态

以下是复审时的四类收尾工作，**不是四组已确认的 partial 缺陷**，也不应据文件长度重新安排机械拆分。第 1 类已在 M13b3、第 2 类已在 M13c2 完成核对，当前余下第 3–4 类。

| 顺序 | 范围 | 具体剩余工作与当前证据 |
| --- | --- | --- |
| 1 | Harmony／Scribe 和外部契约（M13b） | 已完成迁移基线的差异分类、历史日志／回执等价核对和相关单元验证，见 M13b3。复审发现的测试隔离问题已在 M13b2 修复。游戏全量回归仍由用户安排。 |
| 2 | 客户端／服务端组合根及状态归属（A1／A2／A4） | 已完成，详见 M13c2 的状态归属和具名例外清单。M13c1 修复提前绑定，M13c2 将最后一处旧网络门面准入分支迁入传输模块；独立会话／协议及服务端协调器／路由边界组通过。NetworkState 的 39 个构造参数、128 个属性用于显式装配，体量本身不构成残留 partial 耦合。 |
| 3 | 窗口／页面生命周期（A5） | [窗口宿主](../Source/Client/Multiplayer/Windows/ClashOfRimMultiplayerWindow.cs#L12) 持有静态工厂绑定，替换装配时关闭旧页面；[MultiplayerWindowPages.Close](../Source/Client/Multiplayer/Composition/MultiplayerWindowPages.cs#L13) 统一转发关闭。还需将关窗、重开、编辑草稿和会话切换的状态所有权与现有行为测试逐项对应。 |
| 4 | DLC 能力和游戏加载生命周期（A7） | 能力／Ideology 边界单元通过；仍需最后核对 Biotech 编解码唯一来源、Ideology 目录／加载对象与 Scribe 的生命周期归属及允许的游戏适配依赖。 |

### 已复现问题：pawn 测试影响后续难度同步测试（M13b2 已修复）

移除上轮临时诊断输出后，以当前源码编译客户端测试：难度同步测试单独运行通过 **113 个场景**；同进程先运行 `PawnScribeLoaderTests`，再运行 `ClientStorytellerConfigurationTests`，前者通过，后者在 `session.Prepare(captured)` 返回空后报 `Valid Scribe difficulty rejected.`，进程退出 1。

这是可复现的测试间状态／运行时隔离问题，不能据此断定游戏正常执行会失败。调查入口为 [PawnScribeLoaderTests 的原生／泛型 Harmony 补丁](../Tools/ClashOfRim.Client.Tests/PawnScribeLoaderTests.cs#L33)及其 finally 清理，以及 [难度基线恢复](../Source/Client/MainMenu/WorldSetup/ClientStorytellerRuntime.cs#L79)。审计时尚未确认根因；后续 M13b2 已修复共享泛型补丁和测试性能分析环境问题，保留注册顺序及原断言。它不增加 partial 剩余数量。

本次新增证据：

- `LocalOnly/module-migration/partial-reaudit-client-boundaries.log`：客户端边界组通过。
- `LocalOnly/module-migration/partial-reaudit-server-boundaries.log`：服务端边界组通过。
- `LocalOnly/module-migration/partial-reaudit-storyteller-alone.log`：难度同步单组通过。
- `LocalOnly/module-migration/partial-reaudit-scribe-isolation.log`：两组按当前注册顺序运行，复现失败。

此前已产生且本次核阅的证据：`final-recovery-ownership-tests.log` 中九组恢复归属相关单元通过；`final-journal-static-audit.log` 核对历史日志、回执和应用顺序；`final-journal-receipts-tests.log` 中回执恢复组通过；`final-contract-inventory.log` 记录契约差异数量。这些没有在本次重新全跑，也没有仅凭静态差异计数关闭契约验收。

本次复审只更新审计记录，保留其他既有未提交工作；未运行全量回归或实机 RimTest。整体结论是 **partial 拆分剩余 0 组，最终验收仍有四类工作**。复审发现的测试隔离阻塞后续已按 M13b2 处理。

## 本轮已完成的三个实现步骤

| 提交 | 结果 | 本步验证 |
| --- | --- | --- |
| `4aae8aff` | 兼容 API 六个分片合并，保留能力对象和统一注册作用域 | 134 个成员逐 token／顺序比对；7 个相关客户端单元组 |
| `1183cf28` | 银行待确认操作归入同一账务所有者 | 60 个成员及同文件外部契约逐 token 比对；3 个银行／待确认批次单元组 |
| `0f1affba` | 五处单声明 partial 修饰符清理 | 只有五个关键词变更；3 个载荷／支援／交易传输单元组 |

扫描 `Source`、`Tools`、`ThirdPartyCompat/Source` 共 **1,912 个 C# 文件**，partial 类型／声明／文件为 **0／0／0**；整个 ThirdPartyCompat 也经文本交叉检查。DTO 文件中既存关系字段的未提交修改不属于本次提交。冻结 1.6、RimTest 子仓库以及其他既有工作未纳入这三步变更。

## M13a：边界、路由、兼容 API 与冻结文件

本表保留 M13a 时点的验收范围；后续关闭情况以顶部状态表及 M13b／M13c 记录为准。

| 原计划要求 | 当前证据 | 验收范围及剩余工作 |
| --- | --- | --- |
| A1／A4：客户端领域拥有状态，协议和临时认证独立 | 本轮客户端边界组通过；检查 1,702 个纯模块／生成类型，禁止 Mod、全量 settings、综合协议客户端和 IServiceProvider 等依赖 | 已证明守卫覆盖范围内没有禁用依赖；仍需核对宿主／兼容例外及原状态转移的最终归属，不能由类型数量推断全部所有权 |
| A2：服务端业务不依赖宿主或全量状态 | 当前服务端边界组通过，覆盖 466 个模块类型；宿主、提交反向依赖和业务 HTTP 泄漏检查通过 | NetworkState 的 39 个构造参数和 128 个属性仍用于装配；业务目录文本检索未发现全量状态／服务器／IServiceProvider 引用。最终仍需核对明确例外与组合根 |
| A3／M9：统一授权、变更锁、锁外准备和提交所有权 | 当前协调器单元组通过授权、锁外准备、嵌套、重试和失败；银行固定待确认批次与失败回滚组通过；LegacySnapshotRecoveryAdapter 已无源码引用 | 已核对上述边界。日志 1–12、回执优先、pending／applying 唯一所有者及原业务应用顺序仍需从迁移基线复核 |
| A5：窗口与页面独立 | 本轮客户端守卫检查 213 个页面／适配类型、9 个窗口宿主类型及 318 个事件适配类型，通过 | 已证明守卫范围内的依赖约束；页面关闭、编辑草稿及切换状态的所有者仍需结合当前源码与既有相关单元证据核对 |
| A6／M10：保留第三方兼容 API | 从最初基线 `ee55d0b0` 比对，110 个公开成员全部保留，现为 111 个；签名、默认参数和属性访问器无移除／变更 | 唯一新增是 RegisterPawnPreviewRestorer；注册契约文件仅新增 PawnPreviewRestorer 委托（`abf1b597`）。去掉这一明确新增声明后，原注册契约 token 全部一致。五类能力与注册作用域单元组已通过 |
| A7：DLC 按能力拆分，共用元数据与加载生命周期 | 当前能力依赖守卫覆盖 127 个类型并通过；世界、pawn、物品、远程地图能力组通过 | 纯结构边界已有证据；Biotech 编解码唯一来源、Ideology／Scribe 生命周期仍需最后源码核对，不能据本轮门面测试替代所有 DLC 实机结果 |
| A8：桥接命令稳定，场景与 fixture 独立 | 当前桥接组通过 58 个命令契约、15 个模块的宿主／兄弟模块依赖禁止、准入与观察隔离；场景目录单元组通过。客户端守卫另核对 Mod 的 45 个动作重载与 47 个属性契约 | 已核对命令注册和目录边界；原有八组 Save fixture 的实施／行为证据见主计划 M12c1–M12c8。游戏内全部命令执行效果不由这些检查证明 |
| A9：内聚注册表、DTO 不再人为分片 | 两组生产归并及五处修饰符清理已提交；扫描为零，相关单元通过 | 本项完成；不是所有非 partial 类型都已审计的声明 |
| HTTP 路由、方法和类型 | 当前实际注册的 81 个端点通过契约测试；与最初端点清单 `acfae18e` 比对，Kind／Route／Method 全部一致 | 恢复元数据有 16 项从无标记变为显式归属，不能表述为元数据从未改变；见下文。JSON 字段／枚举契约仍需另核对 |
| Harmony／游戏存档契约 | 本轮未改变补丁或存档字段；已有相关实施记录 | 仍需从最初迁移基线逐项核对目标、回调、Scribe 键／默认值和装配顺序，当前证据不足以关闭此项 |
| 冻结 1.6 | `Tools/Test-FrozenLegacy.ps1` 检查 77 个文件清单与 SHA256 通过；`ee55d0b0..HEAD` 的三个冻结目录无差异 | 冻结内容核对通过，不重建冻结产物 |
| 六条验收标准中的测试与提交纪律 | 三步分别在相关单元通过后提交；共 13 个实现相关测试组，另运行本轮四个审计相关组 | 没有全量回归、全量 NetworkSmoke、实机 RimTest 或独立发布／桥接构建；全量仍由用户安排 |

### 恢复元数据差异的解释

变化项为玩家／成就查询、外交创建／回应、两类世界标记同步、站点登记、搬家预检／确认、废弃殖民地、聊天发送／读取、兼容基线覆盖、管理员状态／配置／动作，共 16 个端点。HTTP 路由、方法与协议类型未变化。

当前 [ServerRequestPipeline](../Source/Server/ClashOfRim.Network/Server/Hosting/ServerRequestPipeline.cs#L30) 对没有标记的入口执行统一恢复回退；标记代表入口自行决定验证及恢复顺序，不代表必须恢复。已核对聊天入口有标记，`ChatService` 使用 `EnterUnrecovered`，避免读取／发送触发无关快照恢复。这也说明仅比较清单为绿色不能替代业务顺序验证。其余变化项需要逐域对照原实施证据和当前授权／恢复调用，不能一律视为已完成或回归。

### 本轮证据位置

- `LocalOnly/module-migration/compatibility-facade-static-review.log`、`compatibility-facade-consolidation-tests.log`：门面成员等价及七组单元。
- `LocalOnly/module-migration/bank-registry-static-review.log`、`bank-registry-consolidation-tests.log`：银行归并等价及三组单元。
- `LocalOnly/module-migration/partial-modifier-static-review.log`、`partial-modifier-contract-tests.log`：修饰符精确差异、既有修改隔离及三组单元。
- `LocalOnly/module-migration/final-boundary-audit-tests.log`：协调器、服务端边界／81 个端点、场景目录三组，退出 0。
- `LocalOnly/module-migration/final-bridge-boundary-audit-tests.log`：桥接命令及依赖组，退出 0。
- `LocalOnly/module-migration/final-compatibility-api-contract-audit.log`：从最初迁移基线核对公开 API 和注册契约，已明确分类新增预览 API，退出 0。
- `LocalOnly/module-migration/final-frozen-legacy-audit.log`：77 个冻结文件校验。

## M13b1：独立客户端单元项目漏接编解码模块（已修复）

最终验收实际运行 `ClashOfRim.Client.Core.Tests` 时复现三个 CS0103：`ClientSnapshotPackageBuilder` 已改用拆分出的 `ClientSnapshotPayloadCodec`，但该独立项目只链接了打包器，没有链接新的编解码模块。来源为 `05729f1c` 的编解码复用，正常客户端项目按目录编译，所以仅运行客户端测试无法发现这个独立项目的构建缺口。这违反了不构造完整游戏也能验证纯模块的验收要求。

在 Client.Core.Tests.csproj 中增加对同一生产 `ClientSnapshotPayloadCodec.cs` 的 Compile 链接，未复制压缩／哈希逻辑，也未引入游戏程序集。现有快照测试继续验证五种文化下的 Gzip 解码、SHA256、lineage、输入不变及畸形 XML；独立测试项目运行通过，退出 0，包括客户端／服务端 JSON 契约、语言头和会话发布检查。既有 WireContractTests 等未提交修改保留，不纳入本次提交。

复现日志：`LocalOnly/module-migration/final-wire-core-build-reproduction.log`；通过日志：`LocalOnly/module-migration/final-wire-core-tests.log`。本次仅修复测试项目接线并运行该单元项目，没有完整回归或独立产品构建。其余最终契约核对继续进行，不能据此关闭最终验收。

## M13b2：修复原生 Scribe 测试的跨类型与性能分析耦合

在当前本地 1.7 程序集和 .NET 10 测试进程中，捕获到的真实错误为 `ScribeExtractor.SaveableFromNode` 尝试将 `RimWorld.Difficulty` 转成 `Verse.Pawn`。`PawnScribeLoaderTests` 对 `Scribe_Deep.Look<Pawn>` 的补丁撤销后，其他引用类型的反序列化仍被影响；Harmony 的 owner 清单已清空，不能据此认为共享泛型运行时代码也已隔离。日志为 `LocalOnly/module-migration/scribe-isolation-native-error.log`。

将游戏泛型读取留在 `PawnScribeLoader.ReadPawn` 这一非泛型适配边界中，测试只替换该边界。生产的 `pawn` 键、读取参数、加载／完成／异常清理顺序均保持；没有复制 Scribe 算法，也没有增加可变全局委托或第二套载荷模型。测试补丁安装纳入原有 try/finally，使安装中途失败也会撤销已安装的替身。

补上撤销全部 pawn 替身后的真实 Scribe 往返检查，读取另一种 `IExposable` 引用类型。临时恢复旧的泛型补丁时，新断言稳定报 `Pawn fixture changed native Scribe behavior for another reference type.`；恢复适配边界后通过。这证明新增检查能够独立检出原缺陷，不依赖后续测试恰好发现污染。红灯日志 `scribe-isolation-regression-red.log`，未调整测试注册顺序。

同时定位并移除四处重复的 `Prefs.LogVerbose` getter 补丁。控制台测试没有完整游戏设置，而 Scribe 的 `DeepProfiler.Start/End` 会访问该 getter；它已被其他调用内联时，随后给 getter 打补丁不能可靠覆盖。统一使用 `NativeScribeTestScope` 保存并临时关闭 `DeepProfiler.enabled`，退出时恢复原值；真实 Scribe 与原有异常／清理断言继续运行。该范围只处理性能分析环境，Scribe 会话仍由各测试负责，不通过全局吞异常或全进程常驻补丁规避失败。

原生行为核对使用 reverse-engineer-anything 技能：REA 确认本地 `Assembly-CSharp.dll` 的 SHA256 为 `eff7d97949f919fb2b151ebfb906e9be749b63410b7dbda78eeebb7972b245c3`（证据 `ev_6fc529c145774af2271351edd8953f176ef7f659cff6b896f9185ac078a41332`）；元数据超出 REA 表示预算，未将缺失信息作为事实。使用既有 ILSpy 单类型输出核对 `ScribeLoader`／`DeepProfiler`，记录为 `LocalOnly/module-migration/current-native-ScribeLoader.cs`、`current-native-DeepProfiler.cs`；错误及修复结果另由实际单元运行证明。

**七个相关客户端单元组通过，进程退出 0**：世界设置生命周期、远程运输菜单／到达、DLC pawn 预览、pawn Scribe 及原生隔离、难度同步（113 场景）、终局成就（201 场景）、支援返回持久状态。日志 `LocalOnly/module-migration/scribe-isolation-related-tests.log`。未运行全量回归、实机 RimTest 或独立发布构建；既有未提交工作及冻结 1.6 保持。本步骤关闭测试隔离阻塞，契约差异归类和组合根／状态所有权的最终验收继续进行。

## M13b3：完成迁移契约差异分类与相关验证

对比原始迁移基线 `ee55d0b0` 与 `e1f5b7cf` 加工作区，使用 Roslyn 同时核对显式 Harmony 特性、按名称发现的 Prefix／Postfix 等回调、动态目标、Scribe 参数、数值枚举及序列化成员。语法清单覆盖 `Source/Client` 和 `Source/Shared/ClashOfRim.Protocol`，并非所有构建条件的完整 MSBuild 语义图；结构核对结合以下实际单元及既有提交记录，不用原始差异数量直接判定破坏兼容。

| 契约 | 核对结果与差异归类 |
| --- | --- |
| 客户端网络字段／服务端协议属性 | 未发现原 ClientNetwork DTO DataMember 或原协议公开属性移除、改写。三个请求新增可空 AuthToken，分别用于外交创建、回应及站点登记，客户端／服务端匹配。现有未提交工作还新增关系桩的种族／pawnKind 两个字段，不归入本轮拆分提交。协议扫描另外包含 BiotechSourceConstraint 的四个、WorldFeatureGeometry 的五个新辅助属性；它们不是九个新增 HTTP 请求字段。 |
| 50 个“移除或变化”的 DataMember | 32 个来自旧窗口私有投影：27 个属性完整迁移，五个（ConfigVersion、Source、Role、Kind、CanonicalXml）在旧类仅被输入转换赋值、没有 UI 消费，已精简；其他业务使用的 CanonicalXml 写配置逻辑仍保留。14 个旧配置读取字段合并为共享 ClientModConfigManifest 的九个成员，JSON 名称保持，Nullable／空集合由统一读取规则处理。另有 DLC 清单字段一个、异种人包字段三个移动归属；异种人三字段逐 token 保留。50 项全部归类，不能表述成删除了 50 个网络字段。 |
| 枚举 | 原有协议枚举数值保持。17 个看似删除的成员全部属于三个 UI 枚举的模块迁移，名称／数字完全一致；协议唯一新增项为尾部的 SessionUnavailable=12。其余新增项来自新模块类型，不据此声称协议枚举大量改号。 |
| Harmony 目标与回调 | 原目标特性保持，六个动态 TargetMethod(s) 的方法体 token 保持。除两个 __instance 回调外，新增的回调扫描补足了七个未显式标注方法特性的签名变化：终局成就的六个回调用 __runOriginal／__state 避免原函数跳过后误颁奖，并保留发射前身份；飞船起飞回调移除只用于旧诊断日志的 targetTile 参数，落地地图仍提供实际 tile／layer。合计九个变更已归类；另一个新增 QuestSignal 补丁属于既存未提交工作。 |
| Scribe | 原有 267 处键及其尾随默认值／LookMode／构造参数全部保留。三处重新绑定分别为待确认成就队列、难度配置、pawn 读取；仍使用原键及层级，相关旧字面量存档和真实往返单元通过。新增四项为支援返回 outbox（returnPrepared、returnPawnDead、returnFinishReason、returnPackageJson），缺失时保持 false／false／空字符串／null。M13b2 的 ReadPawn 只改变原生调用所在方法，不增加存档字段。 |
| 快照日志／回执 | SnapshotCommitIntent（1–12 版本读取）、SnapshotConfirmationReceipt、历史校验及回执键／哈希保持。整个原业务应用体在检查点持久化和回执存储两处明确委托替换后保持 token 顺序；最终 journal→pending→pendingUploads 清理和 applying 的 finally 仍由 SnapshotCommitService 所有。回执目录为 null 的新守卫只支持无持久化目录的既有使用方式。批量／单条确认中断恢复与原始回执组通过。 |
| 81 端点的 16 个恢复标记变化 | 路径、方法、请求类型与初始清单保持。标记表示关闭通用恢复回退、由业务决定是否恢复：玩家／成就和聊天采用无恢复范围；管理员状态只恢复配置发布；世界标记执行本域超时维护；外交、运行时世界标记、站点登记、搬家、废弃、管理配置／动作／兼容覆盖拥有授权及恢复复验。九个对应业务测试组通过，不能把标记解释为所有入口都必须启动快照恢复。 |

本步额外运行 **三个私有清单相关客户端组**（兼容差异报告、配置策略共享清单、DLC 基线发布）以及 **一个飞船搬家适配组**，均退出 0。它们验证本次新完成分类的读取合并和回调语义；没有重跑已通过的七组存档／生命周期、独立核心协议、九组恢复归属及回执恢复测试。终局成就组既有 201 场景包含实际 Harmony 跳过原函数的回调行为，搬家组包含异常禁止未跟踪起飞及 tile 12／layer 7，不能将这些单元等同于真实飞船实机测试。

证据为 `LocalOnly/module-migration/final-contract-{inventory.log,differences.json}`、`final-private-manifest-contract-tests.log`、`final-gravship-callback-tests.log`，以及此前核对的 `final-wire-core-tests.log`、`final-recovery-ownership-tests.log`、`final-journal-static-audit.log`、`final-journal-receipts-tests.log` 和 M13b2 七组日志。兼容公开 API／注册契约、RimTest 命令和冻结 1.6 的已核验证据仍见 M13a。此步仅更新审计记录；未提交既有未提交功能、未推送、未运行全量或实机。

M13b 的迁移契约复核完成；A1／A2／A4、A5、A7 的最终所有权／例外复核尚未关闭，整体目标保持进行中。

## M13c1：组合根发布时机与空依赖修复

以 `0a5455d8` 加工作区重新运行 Roslyn 清单工具：`Source`、`Tools`、`ThirdPartyCompat/Source` 共 **1,914 个 C# 文件**，partial 类型／声明／涉及文件仍为 **0／0／0**。机器清单为 `LocalOnly/partial-module-audit/inventory-m13c.json`。新增的两个文件是普通测试类型，不引入 partial；冻结 1.6、独立 RimTest、bin／obj 不在扫描范围。当前仍有三类最终验收，不能把它们写成三组已确认的 partial 缺陷。

使用真实 `ClashOfRimMod` 构造流程确认两种装配错误：

- 支援和袭击商队菜单在 `worldMarkers` 创建前调用 Attach，绑定记录因此永久保存 null；菜单查找目标时会将其传入 `ClientCaravanGizmoContext.FindTargets` 并解引用。其他使用延迟 lambda 的路径不受这处提前取值影响。
- 包身份检查之前有 34 处静态 Attach；错误、空或 null 包身份虽然最终被拒绝，仍会先替换全局适配绑定。只测领域模块、绕过组合根，不能发现这类发布顺序问题。

将这 34 处发布统一移至全部领域依赖构造完成且包身份通过之后，保持原有领域所有者、调用参数和相对发布顺序，没有增加第二套状态或装配服务定位器。Roslyn 核对全部构造语句的 token 多重集保持，并确认包身份检查之前不再有 Attach。既有检查后的清理、快照通知、Harmony 安装与传输提供者接线保持。

新增 `ClientCompositionTests` 实际构造 Mod，只替换游戏设置持久化和全局安装边界；测试不执行真实 Attach 副作用或完整游戏 Harmony 安装。检查两种文化（en-US／tr-TR）下有效包的不区分大小写准入、所有发布参数非空、支援／袭击共享同一世界标记所有者、每个绑定只发布一次，以及错误／空／null 包不发布绑定、不替换已接受的传输提供者。finally 撤销替身、退订本测试创建的快照观察者并恢复文化和委托。

修复前同组退出 1，记录两个空标记所有者及错误包的 34 次发布；修复后 **四个相关单元组通过，退出 0**：组合根、会话清理（260 场景）、商队菜单延迟所有权、客户端模块依赖守卫。证据为 `LocalOnly/module-migration/client-composition-reproduction.log`、`client-composition-static-review.log`、`client-composition-related-tests.log`。未重跑全量或实机测试，也未推送；其他既有修改不纳入本步骤。

本步关闭这两种已复现装配错误，未据此关闭 A1／A2／A4 的全部所有权验收。A5、A7 的最终核对继续保留。

## M13c2：会话、传输与组合根的所有权验收

从 `a76164cc` 加工作区逐项核对 A1／A2／A4，不以模块命名或 partial 清零替代状态检查：

| 所有者 | 当前职责与实际边界 |
| --- | --- |
| `ClientSessionLifecycle` | 拥有登录身份、sessionId、失效转换和身份所有权；设置写入通过 `ClientSessionIdentityStore`，旧 Mod 属性只读取。退出先切换 `ClientSessionWork` 代次，再逐个复验清理订阅者；旧身份 Logout 捕获原凭证，在发布锁外完成。重入登录可终止剩余旧清理。 |
| `ClientSessionConnection`／`ClientSessionWork` | 前者拥有连接、连接锁和旧帧失效 revision；后者仅拥有全客户端的取消代次、异步上下文和发布锁。锁顺序保持发布锁在前、连接／缓存锁在后；取消旧代次在发布锁外，不把身份复制到静态任务设施中。 |
| `ClientSynchronizationCoordinator`／`ClientLocalMutationCoordinator` | 分别拥有上传同步与本地待确认事务状态。`ClientSnapshotComposition` 只为 Mod 和公开上传兼容入口提供同一同步协调器；多个上传器不各自创建第二把同步锁或第二份设置缓存。 |
| `ClientProtocolTransport`／`ClientSessionWebSocketTransport`／`ClientSetupAuthenticationLease` | HTTP 与 WebSocket 实现各自的传输生命周期；临时登录由 lease 捕获唯一凭证，借用凭证不退出，迟到凭证仍按所属会话限时清理。普通登录和设置不由 lease 替换。旧网络客户端只装配及转发；本步移出其最后一处准入分支。 |
| `ClashOfRimNetworkState` | 组合根创建或接收同一批注册表，再给业务模块传实际依赖；配置由 `ServerConfigurationState`，账本／银行／商店等缓存及持久化由各注册表所有。仅在全部依赖已创建后恢复配置，不在业务模块中反查容器。 |
| `SnapshotMutationCoordinator`／`SnapshotRecovery` | 组合根中的 `Lazy<ISnapshotRecovery>` 只用于解决恢复依赖其他领域模块的装配顺序；惰性值只创建一次。协调器负责授权复验、锁外准备、重入及锁释放，恢复器组合具名领域恢复。旧 `LegacySnapshotRecoveryAdapter` 不再存在；生产业务不回调 NetworkState 或服务器静态入口。 |

### 保留的具名接入例外及退出条件

- **引擎组合根与静态 Attach：** `ClashOfRimMod` 构造函数显式列出的引擎回调、商队菜单、页面、预览及兼容能力绑定，只在包准入和完整构造后发布。它们持有所属模块的窄入口，不通过 `Mod.Instance` 找服务。此例外限于具体接入类型，不豁免整个业务命名空间；当引擎接入支持直接传实例时才能撤掉对应静态绑定。
- **应用级共享入口：** `ClientSessionWork` 和 `ClientSnapshotComposition` 分别保留单游戏客户端的代次／发布边界、唯一同步协调器。`ClientSessionCleanup.Attach` 只是应用寿命内的清理装配，逐项调用同一领域实例的 Reset；它没有第二份业务状态。若将来支持同进程多个独立客户端，应由各自运行时显式持有这些实例，不能给当前各域分别创建代次或锁。
- **既有客户端兼容协议：** Mod 的 RimTest 动作／观察入口及旧窗口工厂、公开 `ClashOfRimModNetworkClient`、`ModSnapshotUploadService` 保留调用转发。旧网络客户端的六个静态提供者只经 `ClientProtocolEnvironment`／`ClientAuthenticationEnvironment` 读取，纯模块只依赖窄端口。保留原因是旧调用者及明确的 RimTest／第三方契约；删除条件是相应调用者迁移并明确升级其接口版本，不能在本次保持兼容的拆分中直接删 API。
- **服务端宿主和插件兼容：** `ClashOfRimNetworkServer`、`PersistentServerState`、`ClashOfRimNetworkState` 负责装配；业务端点仅接收对应模块。旧插件上下文仍含 State，因此仅 `SnapshotPostUploadPluginAdapter`／`RaidEvidenceRequestAdapter` 转换参数，`RaidSettlementOperationExecutor`／`RaidSettlementDeferredScheduler`／`SnapshotPostUploadJobExecutor` 等既有公开入口转发给正式模块。删除条件是插件契约升级且调用者完成迁移。编译后依赖守卫明确禁止业务模块反向引用这些兼容入口及 IServiceProvider，不能用该例外绕过领域依赖。

### 本步修复与验证

复核发现 `ClashOfRimModNetworkClient.StreamSessionAsync` 仍独自检查 `context.IsConfigured`；直接调用 `ClientSessionWebSocketTransport` 时空用户会尝试打开连接。新增独立单元先复现此差异（退出 1），再将同一判定移入传输模块，移除旧门面的分支与私有 NotConfigured 帮助函数。错误文案通过现有模式的窄委托提供，不扩大通用元数据接口，也不在传输层依赖游戏翻译。公开方法签名、NotConfigured 错误码和“未配置优先于取消”的既有结果保持。

新增矩阵覆盖服务器／用户／殖民地缺失各自配合正常／已取消 token，共六种情况，要求不建连、不发事件、错误文案按调用时读取。已有 WebSocket 组继续检查双向失败、帧和文化格式、取消、迟到完成及关闭超时。

本步通过 **九个相关客户端组及两个服务端单元组**：会话生命周期、重连、HTTP 传输、临时认证四组；WebSocket、连接所有权、银行／事件本地变更延续、真实组合根、客户端依赖守卫五组；服务端协调器及模块／81 路由两组。后两组在 Save.Tests 中定向执行，没有运行完整 NetworkSmoke。客户端守卫覆盖 1,702 个纯模块／生成类型，服务端覆盖 466 个模块类型；它们检查编译后签名、方法体和状态机，不只是文本检索。

日志：`LocalOnly/module-migration/final-session-transport-ownership-tests.log`、`websocket-admission-red.log`、`websocket-admission-related-tests.log`、`final-server-composition-tests.log`、`final-coordinator-composition-tests.log`。M13c1 的清理顺序 260 场景证据仍有效，本步未重复运行该组。无全量、实机或独立发布构建；保留其他未提交工作及冻结 1.6，未推送。**A1／A2／A4 本轮模块拆分验收完成；A5、A7 继续。**

## 下一步

继续按顺序核对 A5 窗口／页面生命周期和 A7 DLC 能力／加载生命周期。若发现实际缺陷，按依赖顺序修复并运行相关单元后提交。本表中的未验证项全部补足前，不将目标标记完成。
