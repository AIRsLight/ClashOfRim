# 1.7 架构缺陷与修复记录

基线：`1822e223`，审查日期：2026-10-14。修复仅面向 1.7；主仓库与 ThirdPartyCompat 的 1.6 冻结文件及服务器包保持原始哈希。保留独立殖民地、服务端权威事件和快照确认模型。

## 执行顺序

| 顺序 | 问题 | 优先级 | 状态 | 验收条件 |
| --- | --- | --- | --- | --- |
| 1 | 发布只检查构建和包结构，未执行行为回归 | P2 | 已落实 | PR 执行可移植测试；发布依赖测试成功；本机提供完整客户端测试入口 |
| 2 | 退出后后台响应和主线程动作可能污染新会话 | P1 | 已落实退出与重新登录边界 | 会话取消、迟到响应、排队动作失效与等待者取消均有回归测试 |
| 3 | 快照文件与业务账本分步提交 | P1 | 银行三条路径已修复；其余领域待推广 | 提交中断可恢复，重试幂等，失败不能丢失业务意图 |
| 4 | 全服锁覆盖解压、解析和存储 | P2 | 准备阶段已移出；提交锁保留 | 无状态准备移出锁，提交重验 lineage；并发提交不覆盖新状态 |
| 5 | Mod 和端点大类共享过多状态与职责 | P2 | 首轮边界已抽出 | 会话工作、银行提交、快照准备和远程地图引擎加载有独立入口 |
| 6 | 客户端手写路由、DTO 与共享协议重复 | P2 | 路由已统一；关键 DTO 已加契约检查 | 所有客户端路由读取协议清单；银行、快照、聊天传输模型用实际序列化器验证 |

## 缺陷依据与设计约束

### 1 行为测试未进入发布门槛

`.github/workflows/release.yml` 原流程只构建与验证包。现有 Client、Save、Events 均为控制台测试程序，必须用 `dotnet run` 执行，不能用返回成功但不执行这些用例的 `dotnet test` 替代。客户端完整测试必须使用本地游戏运行库；NuGet 中的引用程序集不能执行游戏代码。

### 2 会话生命周期

`ClashOfRimMod.SessionRuntime.cs` 的普通后台刷新未统一绑定取消与代次。退出会清空集合，但旧响应随后仍可调用 `CaptureWorldMapMarkers`、`MergeChatMessages`；主线程动作也可能跨游戏实例执行。已有重连安全策略保留，新增会话所有权应覆盖结果应用与 finally 清理，防止旧请求清除新请求的运行标记。

### 3 跨存储提交

`RepayBankLoanWithSnapshot` 先 `ReceiveSnapshot`，后 `MarkRepaidWithSnapshot`。文件原子替换只保证快照文件完整，不保证与 SQLite 业务记录共同提交。中途崩溃可能留下扣钱快照和未还贷款。待确认恢复只处理已有 Pending 记录，不能推导未持久化的业务意图。改造必须有持久化意图或共同事务边界；不能只加进程内锁。

### 4 快照锁范围

`ReceiveSnapshot` 在 `RaidSettlementSnapshotMutationGate` 内执行解压、哈希、XML 索引和存储。优化必须保留提交时的连续性、lineage 和袭击结算互斥检查；不能只将原调用移出锁。存储层共用锁也需要单独评估。

### 5 职责边界

`ClashOfRimMod` 的 partial 文件共享会话、经济、世界、聊天和 UI 状态。`ClashOfRimNetworkState` 是全量依赖集合，端点直接编排持久化。优先抽出前述修复需要的真实服务，避免只重命名或移动文件。远程地图投影中的 Scribe、Current.Game 和反射调用应有独立的游戏运行时入口。

### 6 协议重复

`ClashOfRimModNetworkClient` 手写路由，`ClientNetwork/Dtos` 与 `ClashOfRim.Protocol` 同时定义传输模型。先共享路由；DTO 迁移必须保持现有 JSON 字段、枚举、可选值、默认值兼容，并以两端实际序列化器验证。1.6 冻结客户端不重新编译。

## 验证记录

### 本轮实现

- `Tools/Test-Regression.ps1` 执行 Events、Save、Client.Core 和 NetworkSmoke 的独立管线场景。`-IncludeClient -RimWorldManagedDir <游戏目录>` 追加完整客户端测试。PR 使用可复用 `tests.yml`，发布任务依赖该工作流成功。
- `ClientSessionWork` 为后台异步任务传播所属会话，HTTP/WS 请求关联取消令牌；等待完成后检查会话，过期任务不再执行错误状态和 finally 状态更新。两个主线程队列执行前检查会话，游戏内队列还检查游戏实例。快照上传等待与管理员基线等待在动作丢弃后会取消，避免占住上传信号量。退出与主菜单重新登录共享状态清理。
- `BankSnapshotCommitService` 覆盖借款、贷款偿还、债务偿还三个带快照端点：校验并重验 → 写入持久化意图 → 发布快照 → 持久化银行记录 → 发布内存状态 → 执行后处理 → 删除意图。启动、后续请求和袭击后台快照变更前恢复未完成提交。恢复检查父快照及 lineage，遇到较新状态会拒绝覆盖。业务记录写入失败不会提前发布内存缓存。
- 提交日志位于服务器数据目录的 `bank-snapshot-commits/`，格式版本为 1。该目录属于服务器持久化数据，备份时与 SQLite 和 snapshots 一起保留，不能当缓存删除。数据版本 8→9 没有 SQL 布局变化，但要求具备日志恢复能力的服务端，阻止旧版服务端绕过恢复。第三方后处理器必须保证同一快照重复调用幂等。
- `SnapshotUploadReceiver.Prepare` 执行解压、哈希和索引重建；`CommitPrepared` 在当前状态上重新检查身份、历史重放、lineage 和游戏连续性。准备并发限制为 CPU 数量与 4 的较小值。仍保留提交阶段的全局锁和文件存储锁，避免破坏跨殖民地袭击结算顺序。
- `RemoteMapRuntimeLoader` 集中远程地图的 Scribe 装载、外部引用登记和游戏地图挂载；反射字段缺失或 Scribe 忙碌时，在修改引擎状态前拒绝加载。投影器保留 XML 准备和投影编排。
- 客户端路由统一从 `ProtocolContractManifest` 获取。新增使用 DataContractJsonSerializer 与 System.Text.Json 的银行请求、快照元数据双向往返及聊天响应检查，覆盖非默认值、中文、可空值和 lineage。

### 已执行验证

- `Tools/Test-Regression.ps1 -IncludeClient -RimWorldManagedDir E:/SteamLibrary/steamapps/common/RimWorld`：Events 50 项、Save 74 项、客户端核心 5 组、完整客户端 19 项及 NetworkSmoke 独立管线场景通过。
- 银行恢复测试使用真实 SQLite 和快照文件，分别在保存快照前、保存后、后处理和 SQLite 写入阶段注入失败，再重新打开持久化状态；验证恢复、重复恢复、幂等键归属和失败缓存隔离。
- 并发测试先并行准备同一殖民地的两份快照，再竞争提交；只允许一份成功，另一份必须因 lineage 或历史重放校验失败。
- 主客户端与第三方兼容包重新构建；`Test-FrozenLegacy.ps1` 验证全部 77 个冻结文件，`Test-ModPackages.ps1` 验证主包 68 个、兼容包 8 个冻结文件及 1.6/1.7 加载隔离。
- 完整 NetworkSmoke 在缺少未入库的 `SaveSample/SaveHediffFixture.rws` 时不能运行到底。CI 明确执行不依赖该文件的 `--snapshot-pipeline-only` 子集，不将其标作完整联机测试。

### 后续修复顺序

1. 将持久化提交意图推广到礼物、交易、商店和雇佣，分别定义可重放业务记录和后处理幂等约束；本轮银行日志不为这些端点提供跨存储原子性。
2. 补入可分发的完整网络测试样本与 HTTP 银行端点测试，进行实际双客户端退出、切服、还款恢复和袭击结算验证。
3. 测量提交锁等待，再评估按殖民地分锁；跨殖民地结算必须固定加锁顺序。未进行吞吐压测，不宣称性能提升倍数。
4. 继续将银行、交易用例与 UI 状态从 Mod/端点 partial 类中抽出；逐步收敛 DTO，扩展契约检查覆盖面。
