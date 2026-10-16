# 玩家账户、殖民地代际、成就与排行榜验证

环境：Steam RimWorld 1.7.4980 rev60，RimTest 独立双客户端、回环服务器、SQLite 与独立存档。1.6 冻结内容不修改。实机使用已有工作区及 RimTest 未提交改动；精确输入以各运行的 `inputs.json` 为准。

## 修复：废弃后旧在线会话仍可保活

`AbandonPlayerColony` 成功后，原实现删除快照、写入墓碑并撤销认证令牌，但没有终止 `LoginSessionRegistry` 和在线连接。正常客户端收到成功响应后主动取消连接，掩盖了服务器清理不足。

独立端点复现保留原连接，结果为：`accepted=true`、`deleted=true`、`tokenValid=false`，但 `sessionValid=true`、`heartbeatAccepted=true`、`presenceStillOwned=true`、`rebuildSessionAccepted=false`。旧会话可以继续刷新并阻止同账户立即重建登录。复现输出保存在 `LocalOnly/account-session-probe.log`。

修复在持久化删除的应用阶段、原快照变更锁内，按用户和殖民地解析所属会话，复用 `EndSessionConnectionLocked` 终止会话、令牌和在线连接。旧殖民地删除重放不会选中另一个殖民地的新会话。修复后的同一复现中，旧会话/心跳/在线状态均为 false，新登录为 true，见 `LocalOnly/account-session-probe-fixed.log`。

`ColonyAbandonmentBaselineTests` 新增真实快照删除、原连接未主动断开时立即重建、旧令牌重复废弃拒绝、旧删除重放与迟到连接释放不影响新会话的回归。完整 Save 测试 135/135 通过，日志 `LocalOnly/account-save-tests-fixed.log`。其中原有删除中断恢复、迟到登记、登出授权和连接所有权用例一并通过。

## 服务端成就与排行榜

`AchievementLifecycleTests` 覆盖全部 11 个内置成就，在 en-US、zh-CN、fr-FR、tr-TR 下分别验证低于阈值、等于阈值、重复提交、更高值、随后低值与 SQLite 重开；最高值及其来源快照必须保留。累计指标另验证重复来源、零/负增量、九次到十次的阈值，以及同账户新殖民地从零开始。

使用 12 名具有真实快照的玩家验证前十截断、降序及同分时用户/殖民地 ordinal 排序、中文显示名、自己的成就查询、废弃后从榜单和定向查询隐藏、持久化重开后仍保留历史记录。此层直接准备指标与玩家数据，不代表所有 11 种成就在游戏中的触发链都经过实机验证。

## 实机覆盖与边界

实机结果和环境差异在两种配置完成后补充。测试经真实建档、业务操作和十秒废弃确认推进；显式夹具只补足邮费预算、设置资源存储区，以及通过 `Pawn.Kill` 制造雇佣兵死亡。没有直接写入成就、银行或事件账本。
