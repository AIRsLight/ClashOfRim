# 模块拆分最终验收：进行中

本轮代码基线为 `0f1affba` 加工作区，承接[主计划](partial-module-audit-2026-10-17.md)的 A1–A9、M1–M12 和六条每步验收标准。**partial 清理已经完成，整体目标尚未完成验收。** 本表分别记录已经核对的范围和仍需补足的证据，不把编译或结构扫描等同于游戏行为验证。

## 最新复审：还剩多少 partial 耦合

复审基线为 `123056d9` 加当前工作区。重新运行 Roslyn 清单工具，覆盖 `Source`、`Tools`、`ThirdPartyCompat/Source` 的 **1,912 个 C# 文件**，排除 `bin`／`obj`；另对整个 ThirdPartyCompat 进行文本交叉检查。结果如下，冻结 1.6 和独立 RimTest 子仓库不计入本次迁移范围。

| 计数口径 | 当前剩余 |
| --- | ---: |
| 跨文件 partial 类型／耦合组 | **0** |
| 单文件多余 partial 类型 | **0** |
| partial 类型声明／涉及文件 | **0／0** |

最后两组兼容 API、银行注册表分别已在 `4aae8aff`、`1183cf28` 归并；五处单声明修饰符已在 `0f1affba` 清理。旧报告中的 **7／13／11** 是 M12c8 时点的历史数量，不是当前待办。当前机器结果为 `LocalOnly/partial-module-audit/inventory-current-reaudit.json`（空清单）。

本次同时重跑两组边界单元，均退出 0：客户端检查 1,702 个纯模块／生成类型，另覆盖兼容能力 127、Ideology 装配 14、页面 213、窗口宿主 9、事件适配 318 个类型；服务端检查 466 个模块类型、81 个端点及恢复归属。客户端和服务端业务模块目录中未检索到对 Mod、全量 NetworkState、服务器宿主或 IServiceProvider 的直接引用。上述结果证明现有守卫范围内的边界，不足以证明所有跨域生命周期都已验收。

### 仍未关闭的四类验收

以下是四类收尾工作，**不是四组已确认的 partial 缺陷**，也不应据文件长度重新安排机械拆分。

| 顺序 | 范围 | 具体剩余工作与当前证据 |
| --- | --- | --- |
| 1 | Harmony／Scribe 和外部契约（M13b） | 原始 JSON／枚举／补丁／Scribe 差异已有静态清单，历史日志／回执已有等价核对和相关单元证据；尚需完成所有差异分类并关闭存档／补丁验证。当前还存在下面已复现的测试隔离问题，不能宣称相关组合测试全通过。 |
| 2 | 客户端／服务端组合根及状态归属（A1／A2／A4） | [ClashOfRimMod](../Source/Client/Core/ClashOfRimMod.cs#L61) 为 642 行，[NetworkState](../Source/Server/ClashOfRim.Network/Client/ClashOfRimNetworkState.cs#L28) 为 452 行、39 个构造参数、128 个属性。已检查其装配和转发用途，模块边界守卫通过；仍需逐项收拢静态 Attach、延迟恢复装配及状态唯一所有者的例外清单。体量本身不构成残留 partial 耦合。 |
| 3 | 窗口／页面生命周期（A5） | [窗口宿主](../Source/Client/Multiplayer/Windows/ClashOfRimMultiplayerWindow.cs#L12) 持有静态工厂绑定，替换装配时关闭旧页面；[MultiplayerWindowPages.Close](../Source/Client/Multiplayer/Composition/MultiplayerWindowPages.cs#L13) 统一转发关闭。还需将关窗、重开、编辑草稿和会话切换的状态所有权与现有行为测试逐项对应。 |
| 4 | DLC 能力和游戏加载生命周期（A7） | 能力／Ideology 边界单元通过；仍需最后核对 Biotech 编解码唯一来源、Ideology 目录／加载对象与 Scribe 的生命周期归属及允许的游戏适配依赖。 |

### 已复现、仍未修复：pawn 测试影响后续难度同步测试

移除上轮临时诊断输出后，以当前源码编译客户端测试：难度同步测试单独运行通过 **113 个场景**；同进程先运行 `PawnScribeLoaderTests`，再运行 `ClientStorytellerConfigurationTests`，前者通过，后者在 `session.Prepare(captured)` 返回空后报 `Valid Scribe difficulty rejected.`，进程退出 1。

这是可复现的测试间状态／运行时隔离问题，尚不能据此断定游戏正常执行会失败。调查入口为 [PawnScribeLoaderTests 的原生／泛型 Harmony 补丁](../Tools/ClashOfRim.Client.Tests/PawnScribeLoaderTests.cs#L33)及其 finally 清理，以及 [难度基线恢复](../Source/Client/MainMenu/WorldSetup/ClientStorytellerRuntime.cs#L79)。根因尚未确认；后续应修复隔离，不能靠换序、只跑单组或放宽断言将其标记通过。它属于上述第 1 类验收阻塞，不增加 partial 剩余数量。

本次新增证据：

- `LocalOnly/module-migration/partial-reaudit-client-boundaries.log`：客户端边界组通过。
- `LocalOnly/module-migration/partial-reaudit-server-boundaries.log`：服务端边界组通过。
- `LocalOnly/module-migration/partial-reaudit-storyteller-alone.log`：难度同步单组通过。
- `LocalOnly/module-migration/partial-reaudit-scribe-isolation.log`：两组按当前注册顺序运行，复现失败。

此前已产生且本次核阅的证据：`final-recovery-ownership-tests.log` 中九组恢复归属相关单元通过；`final-journal-static-audit.log` 核对历史日志、回执和应用顺序；`final-journal-receipts-tests.log` 中回执恢复组通过；`final-contract-inventory.log` 记录契约差异数量。这些没有在本次重新全跑，也没有仅凭静态差异计数关闭契约验收。

本次只更新审计记录，保留其他既有未提交工作；未运行全量回归或实机 RimTest。整体结论是 **partial 拆分剩余 0 组，最终验收仍有四类工作，其中一个测试隔离阻塞已复现**。

## 本轮已完成的三个实现步骤

| 提交 | 结果 | 本步验证 |
| --- | --- | --- |
| `4aae8aff` | 兼容 API 六个分片合并，保留能力对象和统一注册作用域 | 134 个成员逐 token／顺序比对；7 个相关客户端单元组 |
| `1183cf28` | 银行待确认操作归入同一账务所有者 | 60 个成员及同文件外部契约逐 token 比对；3 个银行／待确认批次单元组 |
| `0f1affba` | 五处单声明 partial 修饰符清理 | 只有五个关键词变更；3 个载荷／支援／交易传输单元组 |

扫描 `Source`、`Tools`、`ThirdPartyCompat/Source` 共 **1,912 个 C# 文件**，partial 类型／声明／文件为 **0／0／0**；整个 ThirdPartyCompat 也经文本交叉检查。DTO 文件中既存关系字段的未提交修改不属于本次提交。冻结 1.6、RimTest 子仓库以及其他既有工作未纳入这三步变更。

## M13a：边界、路由、兼容 API 与冻结文件

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

## 下一步

M13b 先完成剩余恢复归属差异、JSON／枚举、历史日志／回执、Harmony 和存档契约复核，再按上表收拢组合根、状态所有权及例外清单。若发现实际缺陷，按依赖顺序修复并运行相关单元后提交。本表中的未验证项全部补足前，不将目标标记完成。
