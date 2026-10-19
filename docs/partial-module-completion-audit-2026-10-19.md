# 模块拆分最终验收：完成

本轮验收始于 `0f1affba` 加工作区，承接[主计划](partial-module-audit-2026-10-17.md)的 A1–A9、M1–M12 和六条每步验收标准，收尾基线为 `39ee3034` 加 M13d2 测试调整及既有工作区。**本计划范围内的模块拆分与验收完成；游戏全量回归仍由用户安排。** 本表保留各阶段的发现与证据，不把编译、结构扫描或受控引擎替身等同于实机行为验证。

**最新状态（M13d2）：A1–A9 及六条验收标准已逐项关闭，已确认而未处理的本次拆分问题为 0。** 扫描范围内 1,917 个 C# 文件，partial 类型／声明／文件为 **0／0／0**。运输舱夹具和旧状态属性测试的宿主私有字段依赖均已处理。冻结 1.6 的 77 个文件此前校验通过，此后仅改测试及记录。保留的组合根／兼容／引擎入口属于明确接入边界，见 M13c2 的理由及删除条件，不是未迁移的业务实现。

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

以下是复审时的四类收尾工作，**不是四组已确认的 partial 缺陷**，也不应据文件长度重新安排机械拆分。四类现已分别在 M13b3、M13c2、M13c3、M13c4 完成核对。

| 顺序 | 范围 | 具体剩余工作与当前证据 |
| --- | --- | --- |
| 1 | Harmony／Scribe 和外部契约（M13b） | 已完成迁移基线的差异分类、历史日志／回执等价核对和相关单元验证，见 M13b3。复审发现的测试隔离问题已在 M13b2 修复。游戏全量回归仍由用户安排。 |
| 2 | 客户端／服务端组合根及状态归属（A1／A2／A4） | 已完成，详见 M13c2 的状态归属和具名例外清单。M13c1 修复提前绑定，M13c2 将最后一处旧网络门面准入分支迁入传输模块；独立会话／协议及服务端协调器／路由边界组通过。NetworkState 的 39 个构造参数、128 个属性用于显式装配，体量本身不构成残留 partial 耦合。 |
| 3 | 窗口／页面生命周期（A5） | 已完成，详见 M13c3。宿主负责页签及关窗分发，页面拥有草稿和展示状态，领域仍拥有业务状态。15 组专项单元核对关窗／重开、身份与 Game 切换、迟到刷新、权限变化及语言缓存。 |
| 4 | DLC 能力和游戏加载生命周期（A7） | 已完成，详见 M13c4。Biotech 的协议键／元数据编解码／匹配策略保持共享；各能力独立。Ideology 加载、物化、目录的职责及当前 Game／Scribe／调用线程边界已核对，六组相关单元通过。 |

### 已复现问题：pawn 测试影响后续难度同步测试（M13b2 已修复）

移除上轮临时诊断输出后，以当前源码编译客户端测试：难度同步测试单独运行通过 **113 个场景**；同进程先运行 `PawnScribeLoaderTests`，再运行 `ClientStorytellerConfigurationTests`，前者通过，后者在 `session.Prepare(captured)` 返回空后报 `Valid Scribe difficulty rejected.`，进程退出 1。

这是可复现的测试间状态／运行时隔离问题，不能据此断定游戏正常执行会失败。调查入口为 [PawnScribeLoaderTests 的原生／泛型 Harmony 补丁](../Tools/ClashOfRim.Client.Tests/PawnScribeLoaderTests.cs#L33)及其 finally 清理，以及 [难度基线恢复](../Source/Client/MainMenu/WorldSetup/ClientStorytellerRuntime.cs#L79)。审计时尚未确认根因；后续 M13b2 已修复共享泛型补丁和测试性能分析环境问题，保留注册顺序及原断言。它不增加 partial 剩余数量。

本次新增证据：

- `LocalOnly/module-migration/partial-reaudit-client-boundaries.log`：客户端边界组通过。
- `LocalOnly/module-migration/partial-reaudit-server-boundaries.log`：服务端边界组通过。
- `LocalOnly/module-migration/partial-reaudit-storyteller-alone.log`：难度同步单组通过。
- `LocalOnly/module-migration/partial-reaudit-scribe-isolation.log`：两组按当前注册顺序运行，复现失败。

此前已产生且本次核阅的证据：`final-recovery-ownership-tests.log` 中九组恢复归属相关单元通过；`final-journal-static-audit.log` 核对历史日志、回执和应用顺序；`final-journal-receipts-tests.log` 中回执恢复组通过；`final-contract-inventory.log` 记录契约差异数量。这些没有在本次重新全跑，也没有仅凭静态差异计数关闭契约验收。

该次复审只更新审计记录，保留其他既有未提交工作；未运行全量回归或实机 RimTest。当时结论是 **partial 拆分剩余 0 组，最终验收仍有四类工作**；四类后续均已验收，测试隔离阻塞按 M13b2 处理，最终状态见 M13d2。

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

## M13c3：页面状态与生命周期验收

基线为 `9a342b50` 加工作区。核对 `MultiplayerPageFactory`、`MultiplayerWindowPages.Close`、主／管理员窗口宿主、各页面的 Close／Reset 和共用的 `ClientRequestTracker`／`ClientGameRequestTracker`，再与实际行为测试逐项对应：

| 要求 | 当前所有权与证据 |
| --- | --- |
| 宿主不再持有各域编辑状态 | 主窗口只保留工厂／页面集合、页签选择和页签缓存；管理员宿主拥有子页选择及清理分发。每次工厂 Create 产生独立页面，玩家／排行榜、玩家／管理员商店、银行／雇佣只在同一窗口内共享对应展示模型，不跨窗口共享草稿。工厂关系检查加主／管理员宿主行为组通过。 |
| 业务状态不下沉到页面 | 页面读取玩家、成就、商店、银行、交易等领域发布的副本／视图，调用显式领域命令。筛选、行选择、分页和输入草稿由面板或页面拥有；诊断页只投影状态，不修改凭证或触发业务。商店确认同时绑定页面代次和原报价，交易／银行／雇佣延迟操作也经原领域命令复验。 |
| 关窗／重开不复用旧回调 | 统一 Close 向每个子页分发一次；面板 Reset 更换请求 Scope，清理展示缓存、草稿及刷新时间。旧 Scope 的确认、提交和完成回调不能发布到新页面；同一窗口对象重开仍可使用新 Scope。宿主测试验证关闭分发、旧页签回调、重开与替换工厂；各面板测试执行真实命令／迟到完成。 |
| 账号、权限和游戏的边界分开 | 银行、商店、交易、玩家、成就和雇佣用 Game 加会话归属；普通 snapshot 前进不使页面失效。密码草稿属于账号会话，关闭会清空，Game／snapshot 前进不重建账号操作。管理员草稿还绑定权限对象，撤权再授权也不能复用旧操作；相同配置刷新保留编辑，配置变化则替换草稿。 |
| 语言变化和回调重入 | 页签缓存包含 activeLanguage 身份；切换语言重建标签。主窗口在刷新／翻译／页签绘制后复验页面所有者，旧导航不切换新会话页面。配置数值、行编辑和展示测试包含多种文化，避免用文化相关字符串判断配置版本。 |

本步定向运行 **15 个页面相关单元组**，全部退出 0：主宿主、管理员宿主、管理员清单／审计、维护、玩家、商店、配置、账号、诊断、雇佣、银行、玩家商店、交易、成就及玩家面板。测试矩阵包含 user／auth／server／colony／login／generation／Game／close／rebind／snapshot、权限撤销／恢复，以及四种或五种文化下的代表性页面行为。日志 `LocalOnly/module-migration/final-page-lifecycle-tests.log`；不是 15 个游戏内实机场景，也没有运行其他 212 个客户端测试组。

主窗口测试中的私有字段读取只核对装配关系，工作流正确性由实际面板／领域调用及延迟回调断言提供；未将字段同名当作行为证据。M13c2 的编译后依赖守卫仍覆盖 213 个页面／适配类型和 9 个窗口宿主类型，禁止依赖完整 Mod／settings／综合协议客户端。本步没有新增页面框架或重复生命周期算法，只记录已检查的边界及证据。**A5 验收完成；A7 尚未完成，整体目标保持进行中。**

## M13c4：DLC 能力与加载生命周期验收

基线为 `2fe859dc` 加工作区。确认 `BiotechCompatibilityPackage` 只是能力装配：物品引用、编辑和展示、繁殖来源、pawn 交换、远程地图投影／运行时清理、防御点分别由具体组件实现；能力开关通过窄委托读取，不回收进持有整个 Mod 的 DLC 服务。

共享格式的唯一来源为协议 `BiotechThingMetadataKeys`、客户端纯编解码 `BiotechThingMetadata` 和客户端／服务端共用的 `BiotechTradeConstraints`。现有编辑器、选择对话框、物品读取、显示与繁殖来源均调用同一编解码；关键元数据键字面量只在协议键定义中出现。基因运行时、繁殖来源恢复及保存异种人包负责游戏对象处理；它们不各自维护第二份 UI 元数据格式。原有 Biotech 世界生成／污染和物品流转能力继续复用。

Ideology 三层边界已核对：

- `IdeoPackageLoader` 同步执行哈希验证、临时文件及原生加载；不创建或替换 Game，不切换线程，不初始化远程影子，也不注册目录。已有 Scribe 会话不为空闲时直接拒绝，不终止他人的会话；进入自身原生加载后，成功、拒绝、预期异常及向外传播异常均清理自身 Scribe 与临时文件。
- `RemoteIdeoMaterializer` 拥有本地规范化、远程影子初始化及目录发布；本地规范化复用同一加载器并 CopyTo 原对象，不用影子字段覆盖玩家文化身份。引擎调用保留在该适配层，纯包哈希与 DTO 不引入游戏依赖。
- `RemoteIdeoCatalog` 是现存公开的引擎对象索引。它允许为物化过程读取旧目录对象，但有效文化查找检查当前 IdeoManager；物化遇到旧 Game 的对象会注销并重建，不将旧实例插入新 Game。此行为保留给既有存档和对象引用适配，不将索引交给加载器管理；目录并非每次加载后全部清空。原生派系主文化被替换后优先绑定实时派系实例，避免继续使用旧目录实例。

六个定向单元组通过：文化包加载／清理、Biotech pawn／地图、物品来源／恢复、客户端与服务端交易约束、共享元数据、派系文化实例替换。实际矩阵包含四种文化、包哈希大小写／不匹配、Scribe 的四种忙状态、原生拒绝和不同异常、Game／线程保持、旧 Game 目录重建、本地规范化不改变身份，以及能力启闭、包／元数据和基因缓存。原生加载／生成入口由受控替身提供，其后的生产编排和断言实际执行；不将此称为所有 DLC 的完整游戏行为验证。

日志为 `LocalOnly/module-migration/final-dlc-lifecycle-tests.log`；M13c2 的编译后守卫覆盖 127 个能力类型及 14 个 Ideology 装配类型，仍保持有效。本步只提交审计记录，没有新增框架、复制编解码或重复执行全量。**A7 验收完成，进入总验收。**

## M13d0：总验收发现旧运输舱实机夹具残留耦合

总验收基线为 `2985868d` 加既有工作区。重新运行语法清单、冻结文件校验和迁移契约差异分类：1,914 个文件中 partial 仍为零；冻结 1.6 的 77 个文件通过；当前契约的移除／新增项按签名、路径、所有者和绑定与 M13b3 已归类结果一致。此后产品修改限于组合根 Attach 顺序与 WebSocket 准入归属，没有新增协议／存档／Harmony 差异。

核阅 M12c1–M12c8 八组夹具的实施记录、现存测试日志和当前 GameSuite／DomainCommands 装配。GameSuite 通过场景目录绑定执行序列，共享服务器／客户端寿命；DomainCommands 仅登记 15 个独立命令模块，58 个命令契约已有行为与依赖检查。快照日志／回执、兼容 API、银行归并、独立核心协议及相应当前源码的证据仍有效，没有据此重跑整套测试。

**发现一个不能据现有绿色守卫关闭的 A8 缺口：** [TransportPodGiftTests.StartInGame](../Tools/ClashOfRim.Client.Tests/TransportPodGiftTests.cs#L102) 在 129、134 行仍按 `synchronization`、`localMutations` 两个私有字段名从 Mod 取出协调器。它虽调用了真实协调器 API，没有直接改忙碌标记，却仍要求知道组合根的私有存储布局；重命名或移走字段会破坏夹具。这不在 15 个正式桥接命令模块的依赖守卫范围内。

该入口在[原架构修复记录](architecture-remediation.md#L557)中被明确用作临时 `clash.test.transport-gift` 实机夹具，不能仅凭当前没有常规代码调用就删除。它先验证上传／手动同步／本地事务三种忙碌状态下，真实已打补丁的到达方法不会消费到达标记或丢失运输舱／货物，然后检查空闲到达先建立事务再执行引擎清理。普通 `TransportPodGiftTests.Run` 只验证 HTTP multipart，不覆盖这个实机入口；不能用前者通过来声称后者的耦合已经处理。

下一步必须保留这三类忙碌与真实到达断言，移除对 Mod 私有成员名的依赖；应复用正式业务命令／明确的测试装配边界，不增加任意改写宿主状态的反射帮助器，也不新增第二套事务实现。世界标记只读属性也已有类型化入口，可一起消除该处多余字符串反射。此项处理后再完成最终矩阵与目标关闭。

本步证据：`LocalOnly/module-migration/final-acceptance-partial.log`、`final-acceptance-frozen.log`、`final-acceptance-contract.log`、`final-acceptance-contract-differences.json`、`final-acceptance-contract-parity.log`。没有全量或实机验证，没有把“没有 partial”改写为“没有剩余模块耦合”。

## M13d1：运输舱实机夹具显式持有忙碌依赖

将既有礼物模块测试中的 Fixture 提取为共享 `ClientGiftTestFixture`，原礼物和运输菜单测试共同使用；没有复制提交、重试或准入实现。实机忙碌探针通过 `TransportPodGiftContention` 临时绑定使用这些明确依赖的生产模块，三个探针仍调用同一原生 Arrived 并检查到达标记、世界对象及货物。探针不重置实机会话代次，结束后恢复原游戏绑定，再由原绑定执行实际礼物投递。世界标记和事务观察改用已有类型化入口，不新增产品测试钩子。

共享夹具只清理自身状态。会话变化时旧同步租约按生产语义不能释放新代次，因此夹具显式清理自己持有的旧协调器；重入时若引擎绑定已被替换，则不覆盖后继绑定。新单元覆盖三种独立忙碌、每阶段异常、每阶段会话／绑定替换，实际执行生产 Harmony 前缀，末位测试前缀仅阻止依赖活动世界的原生函数体。非礼物到达作为正向对照，确认测试并非无条件跳过引擎边界。

真实 Mod 构造测试同时核对礼物准入、操作、动作及运输绑定与会话清理共用同一同步／本地事务所有者，并连接到公开快照上传所用协调器。这弥补显式忙碌夹具不再读取真实 Mod 字段后的装配覆盖：临时把产品礼物操作接到新的事务协调器时，新增断言确实失败，随后完整恢复产品源码，相关组再通过。

**五个相关单元组通过**：真实组合根、运输菜单／到达、礼物模块、运输舱 HTTP、新忙碌夹具。静态逐 token 检查确认 HTTP 测试、StartInGame 签名、三种忙碌的原生断言和返回回执不变；空闲到达断言仅改为已有类型化事务观察。扫描 1,917 个文件，partial 仍为零。证据为 `LocalOnly/module-migration/transport-fixture-{related-tests.log,composition-red.log,static-review.log,partial-scan.log}`；没有运行实机入口或全量回归，原生函数体和真实服务器投递仍待用户安排实机验证。

此次额外检索发现 `ClientSessionCleanupTests.HostStatusReads` 仍通过 `bankState`、`shopState`、`tradeState`、`mercenaryState`、`eventState`、`diagnostics` 六个私有字段组装未构造的 Mod。它验证旧只读属性而非驱动生产业务，但仍受宿主存储名称影响。下一步将其迁入已有真实构造测试，保留首次读取、默认本地化、显式空状态及领域 Reset 的断言，不为测试扩大产品接口。

## M13d2：旧状态观察使用真实装配，完成总验收

将 `ClientSessionCleanupTests.HostStatusReads` 的断言移入现有 `ClientCompositionTests`。每个案例构造真实 Mod，复用原构造／发布观察设施，直接取得传给清理模块的领域所有者及实际构造的诊断对象；用类型化 getter 读取旧状态属性。删除未构造 Mod、六个私有字段写入以及字符串属性查找，不新增产品接口或通用注入容器。隔离的会话清理夹具也不再创建无用途的诊断对象。

保持原 11 种首次读取顺序、默认本地化、显式空状态、领域 Reset 与四种文化：原 **260** 场景分为 **208 个会话清理场景 + 52 个真实装配状态场景**，没有减少断言。实际领域展示层还使用带 NamedArgument 的翻译重载，测试将它与原无参数重载一起在同一 Harmony 作用域内替换／撤销；否则无活动游戏语言的控制台会在 Reset 后读取默认文本时失败。这是测试环境准备，不改变产品翻译行为。

相关的真实组合根、会话清理、运输舱夹具 **三个单元组通过，退出 0**，日志 `LocalOnly/module-migration/status-properties-related-tests.log`。检索未再发现 `typeof(ClashOfRimMod).GetField/GetProperty` 或构造未初始化 Mod 的测试入口；公开网络提供者的枚举／恢复、桥接属性契约、原生引擎适配及组合关系检查仍保留各自用途。没有全量回归或实机运行，没有修改产品、冻结版本或既有无关改动。

### A1–A9 最终对应

| 项目 | 验收结果与证据 |
| --- | --- |
| A1 客户端主类 | 会话、快照及各业务拥有自己的状态；完整构造后才发布引擎绑定，见 M13c1／M13c2。相关真实组合根及独立领域测试通过。 |
| A2 服务端主类 | 宿主只装配，端点依赖具体业务模块；466 个模块类型与 81 路由守卫通过，恢复惰性装配仍只产生一个所有者，见 M13c2。 |
| A3 统一变更／恢复 | 协调器保留授权复验、锁外准备、嵌套和失败语义；日志／回执／缓存清理次序已核对，旧 LegacySnapshotRecoveryAdapter 已删除，见 M13b3／M13c2。 |
| A4 网络客户端 | 共享 HTTP／WebSocket 传输与临时认证租约独立；最后的 WebSocket 配置准入归入传输模块，兼容门面转发，见 M13c2。 |
| A5 窗口／页面 | 页面拥有展示与草稿，领域拥有业务状态；15 组专项检查关闭、重开、会话／Game、权限、配置和语言变化，见 M13c3。 |
| A6 兼容 API | 单一注册作用域和具体能力注册表；110 个原公开成员及原注册契约保留，新增预览入口已明确归类；七组归并相关单元通过，见 M13a／M12d1。 |
| A7 DLC | Biotech 共享键／编解码／匹配，具体引擎能力分开；Ideology 加载、物化和目录所有权独立，六组专项检查通过，见 M13c4。 |
| A8 测试与场景 | 15 个桥接模块、58 个命令契约，场景目录和八组 Save fixture 已迁移／验证；运输舱与状态属性的最后两处已确认宿主布局耦合分别在 M13d1／M13d2 关闭。 |
| A9 内聚类型／DTO | 内聚的兼容门面和银行注册表归并，五处多余修饰符删除；领域注册表继续独占自己的锁、缓存和持久化，没有分裂为多个状态写入者。 |

### 六条标准最终对应

| 标准 | 结论 |
| --- | --- |
| 1 独立模块和状态所有者 | 通过。独立领域、页面、DLC、夹具用例不要求构造完整宿主；仅专门的装配测试构造真实 Mod。构造依赖和注册表所有权见 A1／A2／A5–A9。 |
| 2 迁出旧实现 | 通过。范围内 1,917 个文件无 partial，旧恢复适配器无引用；门面最后的准入分支已迁出，接入例外及删除条件在 M13c2 明列。 |
| 3 单向依赖 | 通过。编译后守卫覆盖业务及生成状态机，禁止 Mod／全量 NetworkState／服务器／IServiceProvider 回流；页面、能力、桥接及场景另有边界守卫。具名装配／插件适配例外不豁免业务目录。 |
| 4 锁、授权、幂等和恢复顺序 | 通过本次迁移验收。会话发布、协调器和各业务相关单元及日志算法对比保留顺序、复验、重放和锁外等待；M13b3／M13c2 记录具体证据，不推断未执行的实机并发结果。 |
| 5 外部契约和行为断言 | 通过。81 路由、网络字段／枚举、Scribe、日志 1–12／回执、公开兼容 API、Harmony 和桥接契约已逐项归类；最后两处测试改为显式业务依赖／真实装配，原行为断言保留。 |
| 6 专项测试后提交 | 通过。各实施记录列明相关单元与必要编译；本次两个收尾步骤分别验证并提交。按用户要求未追加全量、完整 NetworkSmoke 或实机 RimTest；既有未提交功能不夹带提交。 |

M13d0 的冻结清单、契约差异与已审阅分类的一致性仍有效；之后只调整测试及文档。M13d1 的语法清单仍适用，M13d2 未增删源文件或 partial 声明。此前通过且实现未变的专项未重复执行。

**本计划验收结束。** 后续全量回归、真实游戏启动／UI／网络／原生加载与投递验证由用户安排；这些未执行项是实机覆盖范围说明，不列为仍存在的 partial 耦合。此结论不声称全仓库所有非 partial 类型均无其他架构问题。
