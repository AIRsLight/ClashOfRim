# 1.7 代码质量复审：重复逻辑与防护边界

审计范围：远端 `origin/dev` 的 `1822e223` 至本地 `b058932d`，共 103 个提交、293 个变更文件，32,143 行新增、7,680 行删除。2026-10-16 使用 `git ls-remote` 核实远端提交与本地跟踪引用一致。重点沿客户端会话、服务端经济快照、恢复协调器、账本与故障测试调用链审阅；这不是对所有变更行或游戏功能的完整验证。

审计发现既有正确的共用设施，也有增量修复积累下来的重复编排。主要问题已经表现为校验遗漏和防护失效，不能只按重复行数删代码。以下均未修复，按 Q1 → Q2/Q3 → Q4 → Q5/Q6 → Q7 推进。修复只面向 1.7。

## Q1 · P1：会话防护重复散布，但检查与状态发布没有成为同一个操作

位置：`Source/Client/ClientNetwork/Runtime/ClientSessionWork.cs:50,94`；`ClashOfRimMod.SessionRuntime.cs:734-764`；`Source/Client/Core/ClashOfRimMod.Formatting.cs:126-139`。

- `Observe` 只在返回结果前执行 `ThrowIfExpired`。调用者之后仍在后台线程直接更新状态，例如 `CaptureWorldMapMarkers` 会清空并写入共享标记、礼物/PVP 开关。该方法的 `eventStateLock` 与会话 `Reset` 使用的锁不是同一个。
- 交错为：旧请求通过 `Observe` → 被调度出去 → 主线程退出并开启新会话 → 旧请求继续写入缓存。散布在 catch/finally 的 `IsCurrent` 也只是检查，不能使后续赋值与会话切换互斥。主线程队列的 Guard 能阻止迟到的游戏动作，不能撤销此前已经发生的后台缓存写入。
- 已有 `TryApply` 提供与 `Reset` 共锁的发布边界，但生产代码目前只有快照上传使用；普通刷新仍沿用逐处检查。首轮 `df3d4bfa` 的修复覆盖了“响应回来前退出”，未覆盖“检查后、写入前退出”。

验证：独立编译真实 `ClientSessionWork`，在 Observe 返回后暂停旧任务，Reset 并写入新会话状态，再放行旧任务。直接赋值把状态改为旧响应；通过 TryApply 发布则保留新状态。它验证线程交错和共用设施，不是 RimTest 实机复现。现有 Client.Core 首项只在响应完成前 Reset，无法覆盖这一窗口。

改进：统一会话所有权下的结果发布、失败状态和完成标记清理。需要游戏状态的动作走已有主线程 Guard；后台缓存的发布进入共同会话边界，并明确与缓存锁的顺序。不要继续添加独立 `if (IsCurrent)`；避免在发布锁内执行网络等待或任意耗时回调。

## Q2 · P2：经济端点手写两阶段流程，借款漏掉最终业务规则复验

位置：`Source/Server/ClashOfRim.Network/Server/Endpoints/ClashOfRimNetworkServer.BankMercenaries.cs:72-132,157-195`；`Core/ClashOfRimNetworkServer.BankSnapshotReplay.cs:9-70`；对照 `Core/ClashOfRimNetworkServer.MercenarySnapshotCommits.cs:45`。

借款首次检查开关、金额上限、期限、利率后释放锁准备快照。最终阶段只检查授权、重放、请求 ID 与现有贷款，然后使用先前计算的 `interestSilver`。`CheckCreateBankLoanWithSnapshotReplay` 没有重新检查当前业务配置。雇佣/护卫两阶段共用 Validate 方法，商店则重新读取价格和库存；这些近似流程已经发生漂移。

验证：使用现有经济端点真实 multipart/SQLite/文件快照夹具，在锁外准备阶段通过确定性交错更换配置。关闭借款后请求仍成功；把年利率从 0 改为 60 后仍以 0 利息落账。正常对照也成功。复验会话不等于复验业务条件。

改进：提取借款的单一业务决策函数，在最终提交边界重新求值；保持“已提交重放优先于当前价格/开关和准备失败”的约定。随后将少数经济端点的授权恢复、重放、锁外准备、最终决策、写入意图统一编排，业务校验与响应仍各自明确。补上银行开关/利率/限额/期限变化矩阵；当前 `SnapshotEndpointRecoveryPreparationTests.RunServices` 的条款变化测试只覆盖商店、雇佣和护卫。

## Q3 · P2：重复清理吞掉 Changed，过期拒绝防护实际失效

位置：`Core/ClashOfRimNetworkServer.BankRequestContext.cs:36-46`；`Endpoints/ClashOfRimNetworkServer.BankMercenaries.cs:68,134,521-529`；`Endpoints/ClashOfRimNetworkServer.RaidsDiplomacySupport.cs:1296-1319`。上述 Core/Endpoints 路径均位于 `Source/Server/ClashOfRim.Network/Server/`。

`ReconcileBankRequestContext` 已执行 `ReconcilePendingConfirmationsLocked`，却丢弃返回的清理结果。后续 `TryRejectExpiredPendingConfirmationSnapshot` 再清理一次，第二次看到 `Changed == false`，就不再返回“丢弃未确认进度并重新加入”的拒绝。银行和雇佣快照流程都存在这条链。

验证：放入同一笔两天前的 PendingActivation 债务。借款路径删除债务后继续提交新快照并成功；商店路径保留第一次清理的 Changed 并返回 `PendingConfirmation.ExpiredCancelled`。这说明它不是单纯的额外扫描，而是防护被前一个 helper 消耗掉。

改进：一次清理返回显式结果，由调用者决定是否拒绝。纯读取上下文、到期清理、拒绝策略分开，重放路径也明确是否允许清理。不能只删第二次检查而保留静默取消。加入跨银行、雇佣、商店的同一过期场景契约测试，业务预期独立断言。

## Q4 · P2：生命周期无关分支也恢复，独立入口又主动退化为嵌套恢复

位置：`Endpoints/ClashOfRimNetworkServer.RaidsDiplomacySupport.cs:1264-1274,1322-1341`；`Core/ClashOfRimNetworkServer.SnapshotCommits.cs:19-32`。

`ReconcileAbandonedColonyOnClientLifecycle` 先加全服锁并恢复，再在 Locked 方法里判断是不是 ColonyAbandoned。普通登录、断开也会执行这一轮恢复；同一次生命周期的待确认 hook 还会执行另一轮。这里与事件种类无关的恢复来自本轮增加的包装。

另一个问题是 `ReconcilePendingConfirmationsForColony` 等入口先 `EnterSnapshotMutation` 再调用 `RecoverSnapshotCommits`。协调器检测到已经持锁，将其视为嵌套调用，无法释放调用者的锁，因此独立调用有待恢复载荷时仍会在全服锁内准备。协调器对真实嵌套调用的保守处理是必要的；问题在于这些包装函数主动制造了嵌套。

改进：把与状态无关的事件种类/字段筛选前移；对明确已授权、已恢复的调用链使用 Locked 核心方法。独立入口直接进入可锁外准备的恢复边界。不得全局跳过嵌套恢复，因为后处理重入和新建恢复意图确实存在。验收应断言不相关 hook 不启动恢复，并验证独立调用准备期间能取得全服锁，不宣称未经测量的吞吐收益。

## Q5 · P2：恢复引擎共用，但提交意图的生产仍有 11 份模板

位置：`Source/Server/ClashOfRim.Network/Server/Services/SnapshotCommitService.cs:103-385`。

11 个 Commit 入口分别重复 pending 检查、捕获前版本、构造意图、填 PreparedStoragePayload、Write、pending.Add、Apply。Upload 另有 pendingUploads 计数，部分业务附带 PendingOperations，部分调用有域锁和 Preflight。这些差异应被明确表达，但当前每新增业务都要重新复制通用持久化顺序。

目前没有证据表明这些模板已造成新的丢单，归为维护风险。全类另有读日志版本/互斥字段校验和 Apply 分派，不能把“在 Services 目录”视作已经解耦。服务端仍有 41 个 `ClashOfRimNetworkServer*.cs` partial 文件共享同一类，文件拆分也未约束依赖。

改进：先抽取私有的“注册并持久化固定意图”步骤，集中 payload 校验、写入、内存登记与计数；保留各业务构造、日志格式和预检差异。再按用例缩小服务依赖。不要为去掉几行重复引入大量可选回调的通用事务框架。使用现有崩溃恢复测试验证写入失败不发布缓存、重开恢复和旧日志兼容。

## Q6 · P2：账本的状态变更算法仍随存储后端复制

位置：`Source/Shared/ClashOfRim.Events/Ledger/InMemoryAuthoritativeEventLedger.cs:32-59`；`FileAuthoritativeEventLedger.cs:47-75`；`SqliteAuthoritativeEventLedger.cs:48-87`。

共用 `LedgerBatchValidation` 已统一验证，但内存和文件实现的候选字典、五类变更应用与索引重建几乎逐行相同。SQLite 又手写变更枚举与更新字段。新增批次类型仍要同步修改多个执行器；仅共用验证不能避免某个后端漏应用字段。内存实现甚至保留了“Persist before publishing”的复制注释。

改进：抽取纯函数生成固定的新增/更新记录，内存与文件实现共用候选状态及索引构建；SQLite 保持事务与 SQL 写入职责，消费共同决定而非复制业务状态变换。既有三个后端的参数化行为契约继续保留。文件写入前不发布缓存与 SQLite 事务边界不能为了合并实现而削弱。本项是已确认的重复实现，尚未复现新的后端行为偏差。

## Q7 · P3：测试底座收敛不彻底，重复场景矩阵仍可能漏业务

位置：`Tools/ClashOfRim.Save.Tests/BankRegistryPersistenceTests.cs:134`、`MercenaryRegistryPersistenceTests.cs:120`、`ShopRegistryPersistenceTests.cs:104`；`SnapshotEndpointRecoveryPreparationTests.cs:10-33`。

三个 FailingSlot 除异常文本外相同，已有 `RecoveryTestSupport.Slot` 可承接。故障设施之外，更关键的是共性场景矩阵仍分散：银行只有恢复/授权竞争，商店与雇佣另有价格/容量变更，这次 Q2 就落在矩阵之间。上一轮五组测试设施共用只是第一步。

`LoginSessionRegistry.TryCreate` 在 Source 中已无生产调用，但测试广泛用它初始化会话；它与 Prepare/TryPublish 维护两套创建规则。不要因此把现有测试都改成走完整 HTTP 登录：可让即时创建复用注册表核心，或将其明确限定为测试初始化设施。真实登录发布竞争仍使用专门的 HTTP/注册表测试。

改进：共用故障槽、交错探针与按能力声明的基础场景；各业务的成功结果和持久化断言独立保留。补齐矩阵优先于进一步缩短测试代码，不合并具有不同故障语义的 Store/Slot。

## 不应当删除的重复检查

- 锁外准备前后、恢复发布前后、生命周期回调前后的身份/权限/业务状态复验；这些区间的状态可以变化。
- 重放优先于准备错误；并发请求可能已经提交成功，不能因当前尝试的载荷准备失败而否认已有回执。
- 内存业务预检与磁盘日志加载校验；后者面对重启与损坏数据，是不同信任边界。
- 日志恢复的幂等检查、通知补发和发布前 lineage 检查；不要用“之前检查过”替代当前状态验证。
- 两次相邻授权也不能机械删除：取得域锁期间可能等待，登录会话注册表还允许部分操作不持全服锁。先证明状态和时间边界，再合并执行点。

## 验证与限制

定向探针仅生成在忽略目录 `LocalOnly/quality-audit-20261016/`，未改产品源码或用户已有测试。服务端探针使用当前工作区构建；本报告涉及的借款/上下文/客户端会话源码均与 `b058932d` 一致，工作区其他未提交改动未被算入 103 个提交。探针不是完整端到端游戏测试。

- `dotnet run --project LocalOnly/quality-audit-20261016/Audit.csproj`：正常借款、准备时关闭借款、准备时改利率、过期债务借款、过期债务商店，共五个诊断场景；输出见同目录 `result.log`。
- `dotnet run --project LocalOnly/quality-audit-20261016/client/ClientAudit.csproj`：未受保护发布与 TryApply 发布两个会话交错对照；输出见 `client/result.log`。
- 本轮未重跑完整回归或 RimTest，七个诊断场景中的异常结果用于确认缺陷，不能表述成“功能全部通过”。未修改 1.6，也未推送。
- 登出及连接清理的会话归属问题继续保留在原修复文档的未完成项中；本次未将它重复计入新增的质量发现。
