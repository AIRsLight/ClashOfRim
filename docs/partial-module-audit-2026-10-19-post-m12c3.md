# M12c3 后剩余 partial 耦合审计

审计基线：`93a6a0af` 加本轮开始时的工作区（2026-10-19）。**还剩 5 组测试需要模块化、2 个生产类型需要归并、5 处多余修饰符需要清理。** 另有完成这些步骤后的依赖与外部契约复核。本轮只审计和记录，M12c4 尚未修改实现。

后续进度：M12c4–M12c7 已完成袭击创建、清理、下载、证据及超时相关入口、夹具及共享设施提取，相关单元组通过。当前剩余 **1 组测试、2 个生产归并、5 处标记**，共 **8 个类型、15 处声明、13 个文件**；实施与验证见[主记录](partial-module-audit-2026-10-17.md)。下文及机器清单保留本次审计时点。

## 数量与口径

重新运行既有 Roslyn 扫描器，扫描 `Source`、`Tools`、`ThirdPartyCompat/Source` 下 **1,894 个 C# 文件**，排除 bin、obj、冻结 1.6 和独立 RimTest 子仓库。按命名空间及外层类型区分嵌套类型。[机器清单](partial-module-inventory-2026-10-19-post-m12c3.json)保存声明位置、字段及可解析的跨分片引用。

| 类别 | partial 类型 | 声明 | 不重复文件 | 剩余工作 |
| --- | ---: | ---: | ---: | --- |
| Save.Tests 外层测试及嵌套 Fixture | 8 | 21 | 13 | 5 组模块化，含 3 个嵌套 Fixture |
| 生产兼容门面、银行注册表 | 2 | 8 | 8 | 2 个类型归并 |
| 单声明 DTO／摘要 | 5 | 5 | 3 | 移除 5 处修饰符 |
| **合计** | **15** | **34** | **24** | **5 组模块化 + 2 个归并 + 5 处标记** |

排除单声明类型后，真正跨文件的 partial 为 **10 个类型、29 处声明、21 个文件**。3 个嵌套 Fixture 已包含在对应测试组中，不另算工程；生产代码只有两个跨文件类型。测试外层声明合计 **3,102 行**，已包含嵌套类型，不能再叠加 Fixture 行数，也不代表需要重写这些测试。

对照 GameSuite 收尾后的 `fa5d78ad` 基线，M12c1–M12c3 已完成经济、事件失败报告及礼物拒收三组，数量从 **19／42／30** 降为 **15／34／24**。本次扫描与 M12c3 完成时的机器清单一致；HEAD 的声明文本核对同为 34 处，扫描根之外未发现额外声明。

## 仍需模块化的五组测试

下面的 P2 表示结构改造优先级，不表示本轮复现了运行故障。顺序沿用原计划。

| 顺序 | 测试组 | 外层分片 | 嵌套 Fixture 分片 | 外层类型行数 | 仍未分清的职责 |
| --- | --- | ---: | ---: | ---: | --- |
| 1 / M12c4 | RaidCommitCrashTests | 2 | — | 465 | 崩溃提交／创建请求绑定共用存储、请求、快照及护卫准备 |
| 2 | RaidCleanupCommitTests | 3 | 3 | 621 | 清理／下载授权／下载日志恢复共同操作夹具与故障存储 |
| 3 | RaidEvidenceAuthorizationTests | 4 | 3 | 825 | 授权／在线／请求绑定／恢复共同操作数据库、证据产物及发布钩子 |
| 4 | RaidTimeoutRecoveryTests | 2 | 2 | 564 | 超时维护／日志准备共用时钟、任务、快照和证据故障设施 |
| 5 | SupportClosureTests | 2 | — | 627 | 支援结束／恢复共用业务类型、来源事件、凭据、时间及完成断言 |

### P2：袭击提交测试仍承担公共设施职责

[绑定夹具](../Tools/ClashOfRim.Save.Tests/RaidCreationRequestBindingTests.cs#L150)的构造、重开和调用仍依赖另一分片中的 `Setup`、`Guards`、`Invoke`，生命周期包在外层 `WithStorage` 中。[主入口](../Tools/ClashOfRim.Save.Tests/RaidCommitCrashTests.cs#L14)还隐式运行绑定和独立的 Expedition 恢复验证，无法分别筛选、报告。

`Setup`／`Invoke` 同时被 [Expedition 恢复](../Tools/ClashOfRim.Save.Tests/ExpeditionEndpointRecoveryPreparationTests.cs#L130)、[服务并发重放](../Tools/ClashOfRim.Save.Tests/ServiceSnapshotReplayRaceTests.cs#L24)、[袭击超时](../Tools/ClashOfRim.Save.Tests/RaidTimeoutRecoveryTests.cs#L182)借用；提交测试又借用 `PendingOperationCommitCrashTests.AddAncillary`／`VerifyAncillary`。因此只把绑定类改名，或保留原测试类作为公共工具宿主，都不能完成依赖收敛。

此外，[Setup](../Tools/ClashOfRim.Save.Tests/RaidCommitCrashTests.cs#L240)中的基线接收、解压、注入 lineage、构建临时子快照，与已有 [SnapshotEndpointPreparation](../Tools/ClashOfRim.Save.Tests/SnapshotEndpointPreparation.cs#L7)重复。M12c4 应复用这一底层准备逻辑，另由袭击准备设施负责守方快照、敌对关系及护卫；请求构造与持久夹具分别明确归属，所有现有调用者一起迁移。

### P2：清理、证据、超时的夹具仍跨文件共同维护状态

- [清理 Fixture](../Tools/ClashOfRim.Save.Tests/RaidCleanupCommitTests.cs#L192)拥有 root、Original／Cleaned、Mutation、State、Files／Store；[下载恢复分片](../Tools/ClashOfRim.Save.Tests/DownloadCleanupJournalRecoveryTests.cs#L106)直接依赖这些成员创建意图、切换故障和收集证据。其三个 Fixture 分片须一起收归一个生命周期所有者。
- [证据 Fixture](../Tools/ClashOfRim.Save.Tests/RaidEvidenceAuthorizationTests.cs#L83)拥有数据库、快照、产物存储及处理器回调；[日志恢复分片](../Tools/ClashOfRim.Save.Tests/RaidEvidenceJournalRecoveryTests.cs#L170)直接使用 root、Store、State、StateEvidence、Artifacts。四类验证应独立组合夹具，保留产物准备、发布、授权复验和资源释放矩阵。
- [超时 Fixture](../Tools/ClashOfRim.Save.Tests/RaidTimeoutRecoveryTests.cs#L327)负责时钟、数据库、任务和产物存储；[日志准备分片](../Tools/ClashOfRim.Save.Tests/RaidTimeoutJournalPreparationTests.cs#L121)直接使用 Files、Store、State、Now、Journal。需一起收拢重开与清理，而非为恢复路径再创建第二套状态。

扫描器分别解析出三个 Fixture 的 5、2、3 个跨分片字段访问。这只是已解析的字段证据，**不含全部属性访问**，也不是耦合强度排名；源码中的状态依赖比这些数字更广。

### P2：支援结束夹具仍反向依赖测试类

[恢复夹具](../Tools/ClashOfRim.Save.Tests/SupportClosureRecoveryPreparationTests.cs#L150)直接调用 `SupportClosureTests.Source`、`Call` 及外层完成断言，并引用另一分片的 `Kinds`、`OriginalTime`、凭据帮助方法。主类还持有按 NetworkState 缓存的 `Tokens`，需要明确凭据生命周期，不能只将夹具搬出文件后继续反向调用测试类。

可沿用前两组的案例／请求客户端／持久夹具边界，并复用已提取的目录、存储、发布及授权探针。保持返回、损失、拒绝三个业务分支和原证据比较语义，不为形式统一复制或削弱断言。

## 两个生产类型适合归并

- **ClashOfRimCompatibilityApi：6 个分片、459 行。** [主文件](../Source/Client/ThirdPartyCompatibility/Api/ClashOfRimCompatibilityApi.cs#L15)已经装配独立能力模块；其他分片主要通过这些对象转发。能力状态及注册生命周期拆分已经完成，剩余是把公开门面收归一个文件，保持签名、注册顺序和异常语义。`ClashOfRimCompatibilityApi.Registrations.cs` 是独立契约，不是第七个 partial。
- **BankLoanRegistry：2 个分片、983 行。** [PendingOperations](../Source/Server/ClashOfRim.Network/Server/Registries/Economy/BankLoanRegistry.PendingOperations.cs#L5)依赖主类的 gate、loans／debts、状态常量和 `PersistBankRecords`。同一账务写入边界应保留一个状态／锁／持久化所有者，归回同一类型文件即可；不另拆独立写入服务。

## 五处无跨文件耦合的标记

`ModThingReferenceDto`、`ModCrossMapPawnReferenceDto`、`ModPawnExchangeIdentityDto`、`ModPawnExchangeAppearanceDto`、`ItemDeliveryItemSummary` 均只有一处声明，分布于三个文件。删除修饰符即可，保持 DTO／序列化契约；涉及既存未提交改动的文件只提交本次清理行。

## 已完成部分与验收边界

本轮源码复核确认 GameSuite 已非 partial；经济、事件失败报告和礼物拒收各自的验证入口已在 Program 独立登记，持久夹具拥有构造、重开和作用域清理，也已有禁止反向依赖其他测试类的编译依赖守卫。未把这些已完成组重新列入剩余量；各阶段既有行为测试记录见[实施记录](partial-module-audit-2026-10-17.md)。

后续按上表五组 → 兼容门面 → 银行注册表 → 五处修饰符 → 依赖及 API／协议／Harmony／存档契约复核推进。每组保持原场景矩阵，运行相关单元测试后单独提交；必要时增加单功能 RimTest，全量仍由用户安排。

本轮证据是重新执行的静态扫描、HEAD／工作区声明核对和所列源码检查；未运行单元、实机或全量回归。机器清单采用语法分析及有限符号解析，不是完整 MSBuild 或运行时调用图。本轮也未对全部非 partial 模块重新做架构审计，因此清零上述数量不能等同于清零项目中的所有耦合。
