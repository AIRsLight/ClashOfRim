# 剩余 partial 耦合复核：M12b8 后

审计基线：`5a890a37` 加审计开始时的工作区。结论是仍有 **9 组需要真正模块化的测试代码、2 个适合归并的生产类型、5 处多余修饰符**；不能把 20 个类型都算成尚未处理的业务模块。

后续进度：M12b9 已修复两个草稿验收缺口；M12b10a／M12b10b 已完成 GameSuite 收尾；M12c1–M12c6 已拆分经济、事件失败报告、礼物拒收、袭击创建、清理及证据的验证入口与夹具。当前剩余 10 个类型、19 处声明、15 个文件，后续为 2 组 Save 测试、两个生产归并和五处修饰符清理，验证见[实施记录](partial-module-audit-2026-10-17.md)。较早边界见 [GameSuite 收尾后复核](partial-module-audit-2026-10-19-post-gamesuite.md)；本文正文和机器清单保留原审计时点。

## 统计口径

沿用原 Roslyn 扫描器，扫描 Source、Tools、ThirdPartyCompat/Source 下 1,868 个 C# 文件；排除 bin、obj、冻结 1.6 和独立 RimTest 子仓库。另以文本检索核对 ThirdPartyCompat/ServerPlugins、Shared，没有额外 partial。类型按命名空间和外层类型区分；跨文件关系只包括成功解析的符号，不是完整 MSBuild 或运行时依赖图。[机器清单](partial-module-inventory-2026-10-19-remaining.json)保留每个声明的位置、字段及已解析跨文件调用。

| 工作区类别 | 类型数 | 声明数 | 不重复文件数 | 剩余处理 |
| --- | ---: | ---: | ---: | --- |
| GameSuite | 1 | 2 | 2 | 完成场景提取验收，收拢目录、路由和宿主 |
| Save.Tests | 12 | 29 | 19 | 8 个外层测试组及其 4 个嵌套 Fixture |
| 生产兼容门面、银行注册表 | 2 | 8 | 8 | 保留职责和外部契约，归并文件 |
| 单声明 DTO／摘要 | 5 | 5 | 3 | 移除多余 partial |
| **合计** | **20** | **44** | **32** | **9 组模块化、2 组归并、5 处修饰符清理** |

去掉单声明类型后，实际跨文件为 **15 个类型、39 处声明、29 个文件**。其中 4 个嵌套 Fixture 随所属外层组一起处理，不另计 4 组工程。生产代码合计为 7 个类型、13 处声明、11 个文件，其中只有 2 个类型仍跨文件。

**已提交与草稿必须区分：** `5a890a37` 有 20 个类型、46 处声明、34 个文件，GameSuite 有 4 个分片、394 行。工作区已删除 Relocation、Gravship 两个分片并增加独立场景与共享 fixture，但此步骤尚未验证和提交。此次报告提交只记录审计，不包含这批代码。此前 `258305ca` 时点的 48 处声明／36 个文件是历史工作区数据。

## 1. GameSuite：1 组，仍有两步收尾

当前 [GameSuite.cs](../Tools/ClashOfRim.GameTests/GameSuite.cs#L15) 与 [GameSuite.Stability.cs](../Tools/ClashOfRim.GameTests/GameSuite.Stability.cs#L6) 合计 300 行类型代码、12 个直接方法、44 个字段；18 个字段存在已解析跨分片使用，另有 3 条去重跨分片调用关系。大部分字段是场景依赖，不能把这些数值当作可变业务状态数量。

聊天、外交、外观、成就、排行榜及账户生命周期已提交独立模块，不再重复列为待迁移。剩余责任是：

1. **完成 M12b9 搬家／飞船提取。** 工作区的 `ColonyMoveScenarioFixture` 已统一贷款、库存、pawn、DLC、权威站点和重载观察；两个场景只注入执行设施与具体 fixture，未再接收 GameSuite 或全量 ServerState。但尚缺针对本次改动的单元验证，不能按文件消失判定完成。
2. **完成场景目录与路由归属。** [SuiteProfile.Validate](../Tools/ClashOfRim.GameTests/SuiteProfile.cs#L31) 仍调用 `GameSuite.StabilityCases`，清单与 `RunStability` 分开维护；主类另有 Feature／World／GiftReplay 清单与执行代码。应由同一场景描述支持计划、校验和执行。
3. **迁出剩余组合场景。** [Stability 中的 pawn fixture](../Tools/ClashOfRim.GameTests/GameSuite.Stability.cs#L65)仍准备共享 pawn 句柄并编排支援返回；袭击后重登入服的库存损失断言也仍在宿主。将其归对应场景组合，保留“损失只发生一次”的检查，然后合并仅剩装配和生命周期的宿主。
4. **清理迁移遗留。** 当前两个分片已无调用者的 `Confirm`、`AcceptLanguageWarning`、`Idle`、`Step` 转发可删除；`tradeActions`、`eventFixture` 仅在构造装配时使用，可降为构造局部变量。这属于迁移后的收尾，不需再创建抽象层。

### M12b9 草稿仍有的验收缺口

以下由静态控制流确认，本轮未运行故障测试，也不是产品搬家功能的实机故障结论：

- **P2：检查旧地图观察。** [RelocationScenario](../Tools/ClashOfRim.GameTests/RelocationScenario.cs#L20)移除敌人后仍检查添加敌人之前的 `map`。若清理后仍禁止定居，该检查无法发现。应重新观察目的地地图，并用“移除敌人后仍 disabled”的反例验证。
- **P2：计时异常缺少清理。** [ScenarioPolling.RunUntil](../Tools/ClashOfRim.GameTests/ScenarioPolling.cs#L24)仅在正常完成时调用 `time.cancel`。启动已生效但响应抛错、观察抛错或等待超时都会跳过停止，可能污染后续步骤。启动和等待应共同纳入 `try/finally`，并复用 [MercenaryScenario 召回](../Tools/ClashOfRim.GameTests/MercenaryScenario.cs#L38)已有的清理语义。

这两项在旧搬家／飞船代码中也有对应模式；迁出 partial 本身没有解决它们。补齐有关反例和单元验证后，再提交 M12b9。

## 2. Save.Tests：8 组，尚未开展本轮模块化

| 外层测试组 | 外层分片 | 嵌套 Fixture 分片 | 外层类型行数 |
| --- | ---: | ---: | ---: |
| EconomicEndpointRecoveryPreparationTests | 2 | 2 | 486 |
| EventFailureReportTests | 2 | — | 542 |
| GiftRejectionEndpointTests | 2 | — | 405 |
| RaidCommitCrashTests | 2 | — | 465 |
| RaidCleanupCommitTests | 3 | 3 | 621 |
| RaidEvidenceAuthorizationTests | 4 | 3 | 825 |
| RaidTimeoutRecoveryTests | 2 | 2 | 564 |
| SupportClosureTests | 2 | — | 637 |

合计 4,545 行外层类型代码，已包含嵌套 Fixture，不重复累加。恢复和请求绑定分片仍借用主分片的固定时间、种子数据、请求构造、故障存储及断言；嵌套 Fixture 继续跨文件访问 State、数据库、目录和存储。

代表证据：[经济请求绑定](../Tools/ClashOfRim.Save.Tests/EconomicEndpointRecoveryPreparationTests.RequestBinding.cs#L205)直接修改主 fixture 的账本；[袭击请求绑定](../Tools/ClashOfRim.Save.Tests/RaidEvidenceRequestBindingTests.cs#L148)组合数据库、日志目录、重开和故障存储；[超时恢复](../Tools/ClashOfRim.Save.Tests/RaidTimeoutJournalPreparationTests.cs#L121)直接依赖另一个分片的 Files、Store、State、Now 和 Journal。

按表中顺序逐组收拢 fixture 状态和生命周期，再让不同验证类组合明确入口；继续复用现有 `SnapshotPreparationGateProbe` 和故障存储。真实服务集成 fixture 持有 NetworkState 可以是合理装配，不能仅为了禁止类型引用再加一个全状态包装器。

## 3. 生产代码：2 个归并，5 处修饰符

- **ClashOfRimCompatibilityApi：6 个分片、459 行、110 个直接方法。** 14 个跨分片字段是能力对象引用，能力集合和注册生命周期已由独立模块拥有。剩余主要是公开门面归并，保留签名、注册语义和兼容异常策略。`Registrations.cs` 是独立契约文件，不是第 7 个 partial。
- **BankLoanRegistry：2 个分片、983 行。** [PendingOperations](../Source/Server/ClashOfRim.Network/Server/Registries/Economy/BankLoanRegistry.PendingOperations.cs#L7)与主类共用 gate、贷款／债务集合和持久化。应合并文件，保持单一账务写入所有者，不另拆一个可独立写账本的服务。
- **5 个单声明类型：** `ModThingReferenceDto`、`ModCrossMapPawnReferenceDto`、`ModPawnExchangeIdentityDto`、`ModPawnExchangeAppearanceDto`、`ItemDeliveryItemSummary`。共 3 个文件，无跨文件 partial 耦合；只移除修饰符并保持序列化契约。部分文件已有其他未提交改动，后续只提交此次清理的行。

## 推进顺序与本轮证据

顺序保持：完成搬家／飞船专项单元验证与提交 → 场景目录、组合流程和宿主收尾 → 上表 8 组 Save 测试 → 兼容门面与银行注册表归并 → 5 处修饰符 → 最终依赖和契约复核。清零 partial 不等于所有架构耦合清零；本次没有对整个非 partial 代码库重新审计。

本轮运行静态扫描，独立核对工作区／HEAD 声明计数、声明位置和直接源码依赖，检查报告与 JSON 计数一致。只更新审计文档和清单；未修改实现、未运行单元、专项 RimTest 或全量回归，未把既往结果作为当前草稿验收证据。全量验证仍由用户安排，整体模块拆分目标尚未完成。
