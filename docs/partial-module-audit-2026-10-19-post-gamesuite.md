# GameSuite 收尾后的 partial 耦合复核

审计基线：`fa5d78ad` 加本轮开始时的工作区（2026-10-19）。结论：**还需处理 8 组测试模块、2 个生产类型的文件归并，以及 5 处多余修饰符。** GameSuite 已完成本阶段拆分，不再列入待办；M12c1 尚未开始修改。本轮只核对结构，不新增功能故障结论。

后续进度：M12c1–M12c3 已完成经济、事件失败报告及礼物拒收的验证入口、夹具与共享设施提取，相关测试通过。当前剩余 **5 组测试模块、2 个生产归并、5 处标记**，共 **15 个类型、34 处声明、24 个文件**；最新边界见 [M12c3 后审计](partial-module-audit-2026-10-19-post-m12c3.md)，实施及验证见[实施记录](partial-module-audit-2026-10-17.md)。下文和机器清单保留本次审计时点。

## 数量与口径

沿用 Roslyn 扫描器，扫描 `Source`、`Tools`、`ThirdPartyCompat/Source` 下 1,874 个 C# 文件，排除 bin、obj、冻结 1.6 和独立 RimTest 子仓库；另外以仓库文本检索核对扫描根之外的声明。按命名空间及外层类型区分嵌套类型。[机器清单](partial-module-inventory-2026-10-19-post-gamesuite.json)保留声明、字段和已解析的跨分片引用。

| 类别 | partial 类型 | 声明 | 不重复文件 | 实际剩余工作 |
| --- | ---: | ---: | ---: | --- |
| Save.Tests 外层测试及嵌套 Fixture | 12 | 29 | 19 | 8 组模块化，含 4 个嵌套 Fixture |
| 生产兼容门面、银行注册表 | 2 | 8 | 8 | 2 个类型归并 |
| 单声明 DTO／摘要 | 5 | 5 | 3 | 移除 5 处修饰符 |
| **合计** | **19** | **42** | **30** | **8 组模块化 + 2 个归并 + 5 处标记** |

排除单声明类型后，真正跨文件的 partial 为 **14 个类型、37 处声明、27 个文件**。4 个嵌套 Fixture 属于对应外层测试组，不应再次计算为 4 项独立工程。生产代码只有两个类型仍跨文件，不是尚有 19 个生产模块未拆。

与上一份审计正文的 20 个类型／44 处声明／32 个文件相比，减少的是 GameSuite 的一个类型和两个分片。M12b9、M12b10a、M12b10b 的职责提取、专项单元验证与提交见[实施记录](partial-module-audit-2026-10-17.md)。其余 partial 数量未发生变化。

## 仍需真正模块化的 8 组测试

以下按已有计划顺序排列，行数是外层类型声明范围，已包含嵌套类型，不重复累加。P2 表示结构改造优先级，不代表复现了产品运行故障。

| 顺序 | 外层测试组 | 外层分片 | 嵌套 Fixture 分片 | 外层类型行数 | 剩余边界 |
| --- | --- | ---: | ---: | ---: | --- |
| 1 | EconomicEndpointRecoveryPreparationTests | 2 | 2 | 486 | 恢复／请求绑定共用夹具、请求及账本变更 |
| 2 | EventFailureReportTests | 2 | — | 542 | 失败报告／恢复共用种子、时间、完成断言与目录生命周期 |
| 3 | GiftRejectionEndpointTests | 2 | — | 405 | 拒收／恢复共用事件、时间、账本与目录生命周期 |
| 4 | RaidCommitCrashTests | 2 | — | 465 | 提交崩溃／请求绑定共用存储、端点调用和守卫准备 |
| 5 | RaidCleanupCommitTests | 3 | 3 | 621 | 清理／下载授权／日志恢复共同操作夹具内部状态 |
| 6 | RaidEvidenceAuthorizationTests | 4 | 3 | 825 | 授权／在线／请求绑定／恢复共用数据库、证据和发布故障设施 |
| 7 | RaidTimeoutRecoveryTests | 2 | 2 | 564 | 超时维护／日志准备共用快照、任务、故障存储和时钟 |
| 8 | SupportClosureTests | 2 | — | 637 | 支援结束／恢复共用返回／损失种类、时间及调用、完成断言 |

合计 **4,545 行**。这不是需要重写的行数；已有业务断言和矩阵应保留。核心工作是把夹具的创建、重开、故障注入和清理交给明确的所有者，让各验证类组合夹具。

可复核的耦合实例：

- [经济请求绑定 Fixture](../Tools/ClashOfRim.Save.Tests/EconomicEndpointRecoveryPreparationTests.RequestBinding.cs#L205)直接访问主分片定义的 State 并修改账本，且调用外层测试类的 WithHash；仅将其中一段搬文件不能解除反向依赖。
- [袭击证据恢复 Fixture](../Tools/ClashOfRim.Save.Tests/RaidEvidenceJournalRecoveryTests.cs#L170)同时依赖其他分片的 root、Store、State、StateEvidence 和 Artifacts。三个 Fixture 分片必须一起确定生命周期，不能各自新建状态。
- [超时日志 Fixture](../Tools/ClashOfRim.Save.Tests/RaidTimeoutJournalPreparationTests.cs#L121)直接使用另一分片的 Files、Store、State、Now 和 Journal，形成隐含装配约定。
- 恢复分片借用其他测试类发布的基础设施，例如 [SnapshotEndpointRecoveryPreparationTests.Store](../Tools/ClashOfRim.Save.Tests/SnapshotEndpointRecoveryPreparationTests.cs#L437)、`ServiceSnapshotReplayRaceTests.Race`、`MercenaryCommitCrashTests.PrepareEndpoint`。迁移时应复用其实现，并按实际依赖提取共享测试支持；不要在每个新模块复制一份，也不要只去掉 partial 后保留测试类之间的隐含依赖。

继续复用现有 `SnapshotPreparationGateProbe`、HTTP 测试客户端和故障存储。真实集成夹具持有 NetworkState 本身可以合理；不以新建全状态上下文、服务定位器或大批转发方法作为拆分完成的标准。

## 两个生产类型：归并比继续拆分合适

- **ClashOfRimCompatibilityApi：6 个分片、459 行、110 个直接方法。** [主文件](../Source/Client/ThirdPartyCompatibility/Api/ClashOfRimCompatibilityApi.cs#L15)持有独立能力对象，[远程地图运行入口](../Source/Client/ThirdPartyCompatibility/Api/ClashOfRimCompatibilityApi.RemoteMapRuntime.cs#L13)等分片主要转发给这些对象。能力存储及注册生命周期已经迁出；剩余工作是归并公开门面，保留 API 签名、注册顺序和异常语义。`ClashOfRimCompatibilityApi.Registrations.cs` 是独立契约，不是第七个 partial。
- **BankLoanRegistry：2 个分片、983 行。** [PendingOperations](../Source/Server/ClashOfRim.Network/Server/Registries/Economy/BankLoanRegistry.PendingOperations.cs#L5)仍访问主类的 gate、loans、debts、状态常量和 PersistBankRecords。它们属于同一账务写入边界，适合归回一个类型文件；拆成两个独立写入服务会增加一致性负担。

## 五处修饰符清理

`ModThingReferenceDto`、`ModCrossMapPawnReferenceDto`、`ModPawnExchangeIdentityDto`、`ModPawnExchangeAppearanceDto`、`ItemDeliveryItemSummary` 均只有一处声明，分布在三个文件，无跨文件 partial 耦合。只需移除修饰符，保持 DTO 和序列化契约。部分文件有既存未提交修改，后续必须只提交本次清理的行。

## 推进与验证

顺序保持：**上述 8 组 Save 测试 → 兼容门面归并 → 银行注册表归并 → 5 处标记 → 最终依赖和外部契约复核**。每组完成后运行相关单元测试并提交；需要时才增加该功能的专项 RimTest，全量回归由用户安排。

本轮证据为 Roslyn 静态扫描、源码检查及 HEAD／工作区声明交叉核对；已解析的符号关系不等于完整 MSBuild 或运行时调用图。未修改实现、未运行单元或实机测试，不把此前通过记录当成本轮新增验证。清零 partial 也不等于清零所有架构耦合；本轮没有重新全面审计非 partial 模块。
