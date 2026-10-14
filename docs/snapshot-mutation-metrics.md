# 快照提交锁测量

`ClashOfRimNetworkState.EnterSnapshotMutation()` 继续使用原 `RaidSettlementSnapshotMutationGate` 的同步、可重入 `Monitor`。全部服务端调用点通过该入口计时，没有修改锁顺序或扩大并发提交范围。该作用域必须在同一线程同步释放，不能跨 `await` 使用。

Meter 名称为 `ClashOfRim.SnapshotMutation`，两个 `double` histogram 的单位均为毫秒：

| 指标 | 含义 |
| --- | --- |
| `snapshot.mutation.wait` | 开始等待到取得全局锁的耗时 |
| `snapshot.mutation.hold` | 取得全局锁到准备释放的耗时 |

仅使用编译时调用方法名 `operation` 标签，不记录用户、殖民地、快照 ID 或令牌。一次事务中的嵌套恢复/后处理不额外计数；时间计入最外层持锁样本。只有已完成并释放的作用域产生样本，不能用这些指标发现尚未返回的死锁。回调发生在释放锁之后；订阅者异常不改变已经完成的业务结果。没有监听器时不读计时器，但同步作用域对象仍有分配成本。

## 复现

从仓库根目录使用 PowerShell 7：

```powershell
Push-Location Tools/ClashOfRim.NetworkServer
try {
  dotnet run --project ../ClashOfRim.NetworkSmoke -c Release -- `
    --snapshot-contention-probe ../../LocalOnly/snapshot-contention.json
  if ($LASTEXITCODE -ne 0) { throw 'Contention probe failed.' }
} finally { Pop-Location }
```

探针为每组创建独立 Kestrel 实例、SQLite 银行/事件账本、快照目录和意图日志，其他注册表使用内存实现。每个客户端代表不同殖民地，使用真实 multipart HTTP 上传；每次上传确认一笔待激活债务。每组先预热一轮，再测五轮，等待所有请求结束后进入下一轮。客户端 XML 生成、压缩、父快照准备及待确认债务创建不计入 HTTP 耗时，服务端认证、解压/索引、提交及同步后处理计入。报告统计测量窗口内已完成的锁作用域；不包含服务器启动恢复成本。

探针核对所有响应、最终快照、每个殖民地的六笔已激活债务及无残留意图。任何失败都会终止测量，不能以较快的拒绝响应作为吞吐结果。`SnapshotMutationMetricsTests` 进入默认回归，验证竞争互斥、异常退出、嵌套只计一次，以及订阅者异常不会保留锁。性能探针不进入 CI，不设置机器相关耗时阈值。

## 2026-10-14 本机基线

环境：Windows `10.0.26200`、.NET `8.0.31`、运行时报告 28 个处理器。使用合成服务端 XML，分别额外添加 0/5000 个物品；原始 XML 2017/706207 字节。每组仅 5×并发数个实测请求，不足以代表生产存档、真实磁盘冷启动、长时间运行或全部插件负载。该结果用于定位等待，不能推导容量保证或优化倍数。

| XML 字节 | 并发殖民地 | HTTP p95 ms | `ReceiveSnapshot` 等待 p95 ms | 持锁 p95 ms |
| --- | --- | --- | --- | --- |
| 2017 | 1 | 13.96 | 0.00 | 4.69 |
| 2017 | 4 | 34.91 | 18.15 | 9.15 |
| 2017 | 8 | 64.54 | 43.35 | 8.58 |
| 706207 | 1 | 115.87 | 0.00 | 28.14 |
| 706207 | 4 | 261.06 | 114.17 | 63.51 |
| 706207 | 8 | 445.58 | 210.10 | 66.29 |

706207 字节、8 并发时，40 次 `ReceiveSnapshot` 持锁总计 1656.09 ms；40 次 `ConfirmPendingOperationsFromSnapshot` 等待总计 4863.29 ms、等待 p95 254.74 ms。后处理再次进入同一全局锁也是请求延迟来源。不同线程的等待总和允许超过整体墙钟时间，不能把两者混作锁利用率。原始聚合 JSON 和运行日志在本地 `LocalOnly/snapshot-contention.json`、`LocalOnly/snapshot-contention.log`。

## 分锁评估与下一步

等待随并发增加，说明后续缩小锁范围有必要；当前不能只把外层对象替换为按殖民地的锁：

1. `SnapshotCommitService` 的 `pending` 字典、全局 `applying` 标记和“先恢复所有意图再新建提交”的检查依赖串行协调。先明确单殖民地与跨殖民地意图的所有权和恢复排他性，再改粒度。
2. `FileColonySnapshotIndexStore` 还有自身全局锁，文件发布和 `GetLatestPackage` 的文件读取、解压、重建索引仍在锁内；仅拆外层锁仍会在这里串行。优先把不依赖可变状态的工作移出，保持返回元数据与载荷同版本。
3. 银行、合同、库存、账本仍有共享业务约束。跨殖民地袭击结算必须先收集完整资源集合，按稳定的 `(用户 ID, 殖民地 ID)` 顺序取锁，不能进入单殖民地锁后动态扩大范围；恢复必须使用相同顺序。

本步完成计时入口和基线，保留原全局提交锁。下一步处理存储中尚可安全移出的准备工作，再用同一探针比较；分锁与实机验收仍未完成。
