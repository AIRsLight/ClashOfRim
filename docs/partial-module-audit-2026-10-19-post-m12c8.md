# 剩余 partial 耦合复核：M12c8 后

审计基线：`e7d39465` 加当前工作区。**还剩 2 个跨文件 partial 类型，合计 8 处声明、8 个文件；另有 5 个单声明类型的多余修饰符。** 前者分别是兼容 API 门面和银行注册表，不是 7 组尚未模块化的业务。GameSuite 和计划内八组 Save.Tests 已完成 partial 拆分，Tools 当前没有 partial 类型声明。

后续进度：M12d1 已完成兼容 API 六个门面的归并及七组相关单元验证。当前剩余 6 个类型、7 处声明、5 个文件，跨文件仅银行注册表 1／2／2；本文正文及机器清单保留审计时点，实施证据见[主记录](partial-module-audit-2026-10-17.md)。

## 范围与数量

重新运行 Roslyn 扫描，覆盖 `Source/`、`Tools/`、`ThirdPartyCompat/Source/` 下 **1,918 个 C# 文件**，排除 `bin/`、`obj/`、冻结 1.6 和独立 RimTest 子仓库。另检索 `ThirdPartyCompat/ServerPlugins/`、`ThirdPartyCompat/Shared/`，没有额外 partial。按命名空间及外层类型归组，当前 HEAD 与工作区的 partial 声明清单一致。

| 类别 | 类型数 | 声明数 | 不重复文件数 | 当前性质 |
| --- | ---: | ---: | ---: | --- |
| ClashOfRimCompatibilityApi | 1 | 6 | 6 | 能力模块已独立，公开门面仍分片 |
| BankLoanRegistry | 1 | 2 | 2 | 同一锁、账务状态与持久化所有者仍分片 |
| DTO／物品摘要 | 5 | 5 | 3 | 无跨文件 partial 耦合，只有多余修饰符 |
| **合计** | **7** | **13** | **11** | **2 组归并、5 处修饰符清理** |

相比上一次保存机器清单的 [M12c5 后审计](partial-module-inventory-2026-10-19-post-m12c5.json)，数量从 **12／26／19** 降为 **7／13／11**。M12c6–M12c8 已处理袭击证据、超时恢复、支援结束三组测试及其夹具；本轮没有把已完成的工作重新列为待办。

本轮[机器清单](partial-module-inventory-2026-10-19-post-m12c8.json)保留每个声明、字段及可解析的跨文件关系。类型行数包含空行及嵌套内容；方法数不含属性、构造器，也不等于公开 API 数量。扫描使用组合语法树和有限语义解析，并非各项目完整 MSBuild 依赖图，调用数不能当作全部依赖数。

## 1. 兼容 API：6 个分片，门面归并尚未完成

位置：[ClashOfRimCompatibilityApi.cs](../Source/Client/ThirdPartyCompatibility/Api/ClashOfRimCompatibilityApi.cs#L15)，以及 `.Pawns`、`.ThingReferences`、`.WorldConfiguration`、`.RemoteMaps`、`.RemoteMapRuntime` 五个分片。共 **459 行类型代码、110 个直接方法**。

主文件持有 18 个字段，其中跨分片使用的是 14 个只读能力对象引用；其余是会话、管理权限、世界目录引用以及注册作用域。分片没有另建这些能力的集合或状态，主要把公开调用转发给能力对象。注册代际、令牌、顺序与撤销由 [CompatibilityRegistrationScope](../Source/Client/Modules/Compatibility/CompatibilityRegistrationScope.cs#L10) 管理；世界扩展和 pawn 交换等模块拥有各自的能力集合。

**剩余处理：** 将六个门面分片归并到一个非 partial 类型，保留已经独立的能力模块。保留公开／内部成员签名、默认参数、能力对象初始化顺序、共享注册作用域和异常策略。会话及目录绑定继续引用已有领域对象，不引入第二份状态。[Registrations.cs](../Source/Client/ThirdPartyCompatibility/Api/ClashOfRimCompatibilityApi.Registrations.cs#L14) 定义委托和注册契约，不是第七个 partial，不能随分片误删。

验收使用成员及实现等价核对，以及现有世界、远程地图、pawn、物品和注册作用域的相关单元组。此次审计未执行归并，也未重新运行这些测试。

## 2. 银行注册表：2 个分片，仍有直接私有状态耦合

位置：[BankLoanRegistry.cs](../Source/Server/ClashOfRim.Network/Server/Registries/Economy/BankLoanRegistry.cs#L5) 与 [PendingOperations.cs](../Source/Server/ClashOfRim.Network/Server/Registries/Economy/BankLoanRegistry.PendingOperations.cs#L5)。共 **983 行类型代码、40 个直接方法**；待确认操作分片占 43 行、3 个方法。

待确认操作直接使用主类的 `gate`、`loans`、`debts` 及两个状态常量；捕获依赖 `GetActive`，应用依赖 `PersistBankRecords`。主类统一维护幂等与殖民地索引，`PersistBankRecords` 先完成持久化，再发布缓存和索引变更。扫描器只解析出一条跨文件调用关系，源码还明确存在持久化调用，因此不能仅依据机器调用数评估耦合。

**剩余处理：** 归并到同一注册表，保持单一账务写入所有者。快照确认算法继续复用 `PendingSnapshotOperations.Confirm`，不另造转换逻辑；保留锁范围、确认验证、重放幂等、落盘失败不污染缓存及索引的顺序。这里共享同一份可变状态符合一致性边界，不应再拆出能独立写入同一账务状态的服务。

验收优先使用现有 `BankRegistryPersistenceTests`、`BankCommitCrashTests` 及覆盖待确认操作的相关单元组。此次仅检查源码依赖，未复现新的银行功能故障。

## 3. 五处多余 partial：没有跨文件耦合

| 类型 | 声明位置 |
| --- | --- |
| ModThingReferenceDto | [ModTradeDtos.cs:7](../Source/Client/ClientNetwork/Dtos/ModTradeDtos.cs#L7) |
| ModCrossMapPawnReferenceDto | [ModPawnExchangeDtos.cs:20](../Source/Client/ClientNetwork/Dtos/ModPawnExchangeDtos.cs#L20) |
| ModPawnExchangeIdentityDto | [ModPawnExchangeDtos.cs:95](../Source/Client/ClientNetwork/Dtos/ModPawnExchangeDtos.cs#L95) |
| ModPawnExchangeAppearanceDto | [ModPawnExchangeDtos.cs:111](../Source/Client/ClientNetwork/Dtos/ModPawnExchangeDtos.cs#L111) |
| ItemDeliveryItemSummary | [ItemDeliveryPayloadReader.cs:64](../Source/Client/Gifts/Processing/ItemDeliveryPayloadReader.cs#L64) |

只移除修饰符，保持字段、属性、序列化名称与默认值。后两个文件已有其他未提交工作，清理时只暂存本次修改的行。

## 后续顺序与本轮验证

继续原顺序：**兼容 API 归并 → 银行注册表归并 → 五处修饰符清理 → 最终依赖与 API／协议／Harmony／存档契约复核**。每个实现步骤运行受影响的相关单元测试并单独提交；可按需安排单项 RimTest，全量回归仍由用户安排。

本轮重新扫描并人工核对两组生产类型的状态和调用归属，交叉检查 HEAD／工作区声明与机器清单统计。仅新增审计文档、当前清单并更新主计划索引；没有修改实现或冻结 1.6，没有运行单元、RimTest 或全量回归。

这份清单回答的是仍存在的 partial 耦合。去掉 partial 不等于所有非 partial 模块的依赖都已合理；最终契约复核仍未完成，不能据此宣布整体架构改造结束。
