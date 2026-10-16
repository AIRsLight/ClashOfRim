# 银行、欠款、迁居与 DLC 实机验证

环境：本机 Steam RimWorld 1.7.4980 rev60，独立 RimTest 双客户端、回环服务器与独立存档。1.6 冻结版本不参与修改。测试夹具与玩家操作分开记录；缩短客户端任务截止日期不代表验证服务器长期时间推进。

## 1. 旧债务记录重复还款

- 复现：`G:/RimTestRuns/bank-core-r2`。雇佣真实佣兵后，用明确标注的夹具调用游戏死亡流程，服务器产生死亡赔偿欠款；第一次还清后，再使用旧银行窗口记录还款，客户端再次扣除白银、提前结束本地任务，随后服务器拒绝已结清债务，留下未确认事务。
- 修复：还款前按照债务 ID 从当前银行状态解析仍处于 Active 的债务；金额也取当前记录。已结清或消失的旧记录在扣款前拒绝。
- 验证：`G:/RimTestRuns/bank-core-fixed/20261016-152007-346a9664`，17/17 场景通过，包含贷款边界、两次真实逾期处罚与复利罚息、催收机械族出现、处罚间隔、存读档、余额不足、足额还款、重复还款、佣兵死亡债务、带债务重连及运行时错误检查。
- 夹具边界：只缩短本地到期/下一次处罚时间，处罚阶段、利率、间隔及原版袭击执行保持实际配置；替换测试殖民地白银余额用于不足额与精确扣款断言。尚未覆盖所有处罚阶段及真实数日离线计时。
- 全五 DLC / 中文 zh-CN 与英文 fr-FR 客户端：`G:/RimTestRuns/bank-all-dlc/20261016-152559-2cc0d0d7`，17/17 通过。

## 2. 已有任务地点迁居

- 复现：`G:/RimTestRuns/relocation-core-r2`。空地远行队迁居通过；生成 ItemStash 地点并使殖民者到达其地图后，原版定居命令仍显示“Your faction can only have one base.”，没有进入模组迁居流程。
- 修复：为 `SettleInExistingMapUtility.SettleCommand` 接入迁居；两种目标复用确认、预检、原子区、旧据点清理、快照与服务器确认，只有原版定居动作不同。保留任务地点本身的可定居检查，并补执行原版被基地数量提前返回遮住的殖民者/敌人检查。
- 验证：`G:/RimTestRuns/relocation-core-mainthread/20261016-152750-aa1bf692`，16/16 通过。两条路径均检查三名殖民者身份、携带白银/钢铁、单一新据点、服务器位置、殖民地账号、原贷款及搬家后还款、存读档；任务地点另验证敌人存在时禁用迁居。
- 夹具边界：夹具创建附近合法远行队/ItemStash 地点及其到达状态；没有覆盖所有随机任务的剧情、清场或前往目的地的路途。

## 3. 迁居清理旧地图跨线程

- 复现：`G:/RimTestRuns/relocation-core-fixed/20261016-152336-1aef2bc6` 的业务断言通过，但运行时错误门禁失败。调用栈为 `FinishLocalColonyRelocation → Abandon → MapDeiniter.PassPawnsToWorld → TryGainMemory → MoteBubble → MaterialPool`，报 `Tried to get a material from a different thread.`。
- 修复：地图生成后的迁居收尾长事件改在主线程执行。旧地图清理触发的心情/材质操作不再运行在后台生成线程。
- 验证：同上 `relocation-core-mainthread`，包含运行时错误门禁，全通过。
