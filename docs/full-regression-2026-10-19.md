# 2026-10-19—20 模块拆分后全量回归

**本轮已结束，尚未全绿。** 完整目录的 36 个 profile 均已尝试，原构建共 549 次通过、42 次失败、64 次因前置失败未执行。修复版本的 9 组定向补测共 208/208 通过；合并不同构建的证据，75 个独立用例中 73 个至少通过一次，剩余两个飞船用例被显示条件阻断。原生 Play 场景重建空引用及日志采集缺口仍未解决。

本次按用户明确要求执行完整单元/网络回归和整个 RimTest 场景目录。起始提交为 `93f4968d`，测试包含启动时已有的未提交修改；这些修改未被本次提交收走。不能把结果视为只针对干净的 Git 提交。

## 环境与证据

- Windows、Release、RimWorld `1.7.4980 rev60`，游戏路径 `E:/SteamLibrary/steamapps/common/RimWorld`。
- RimTest 提交 `143559423dc8cee5d46a694186743b311c15c0e5`，包含原有未提交修改。
- 标准回归日志、基线状态、运行时哈希：`LocalOnly/full-regression-2026-10-19-after-modules/`。
- 场景目录为 36 个 profile、75 个独立用例 ID、计划 655 次用例调用；逐项计划见本地 `planned-cases.json`。
- 每场使用独立双客户端、服务数据库和存档，`inputs.json` 记录实际构建哈希及工作区状态。普通场景使用 Headless，飞船场景保留渲染并隐藏窗口。
- 完整矩阵保持 `6c79bfa7` 构建的客户端 DLL，SHA-256 为 `F06A1EAE7264C2FD12FAD5491DCA2023CBAE1ED5235636081C28608734DB8026`。底图修复后使用独立包补测，客户端 DLL 为 `64AC8843715DCE7C65C121D64766DB96E875BD0184F854DCC8E7C030D10B73C7`；不在运行中替换完整矩阵的文件。

## 单元、网络与冻结验证

| 项目 | 结果 | 证据 |
| --- | --- | --- |
| Events.Tests | 51/51 通过 | `regression-rerun3.log` |
| Save.Tests | 246/246 通过 | `regression-rerun3.log` |
| Client.Core.Tests | 10/10 通过 | `regression-rerun3.log` |
| 完整 NetworkSmoke | 通过，退出码 0 | `network-full-rerun.log` |
| Client.Tests | 228/228 通过，退出码 0 | `client-full-world-fixed.log` |
| RimTest 框架便携测试 | 41/41 通过 | `rimtest-portable.log` |
| 1.6 冻结清单与哈希 | 77 个文件通过 | `frozen.log` |
| 两个包的 1.6/1.7 资源隔离 | 通过 | `mod-packages.log` |

标准入口最初在过时测试处停止。修复后分别完成受影响套件；服务端套件通过后仅有客户端和测试设施变更，没有重复执行耗时的 Save 全套。客户端最终从头执行了全部 228 组，包含两处实机死锁及权威底图完整替换修复。

## 回归中发现并修复的问题

1. 源码守卫仍读取已移走的 partial 文件：改为实际模块目录，并保持跨模块扫描。
2. 废弃殖民地和网络授权测试仍期待通用拒绝码：失效令牌应为 `SessionUnavailable`，保留缺失/冒用令牌及无副作用断言，并统一五组令牌测试数据。
3. 文化预览、pawn 交换捕获使用转发 API 的 Harmony 替身，Release 组合执行时会被内联调用绕过：改用有所有者和撤销生命周期的真实兼容注册。
4. 商店和管理员基线测试替换泛型 `DefDatabase<ThingDef>`，影响后续 `GeneDef` / `FactionDef`：统一使用真实定义库隔离夹具，保留筛选与缓存失效验证。
5. 远程地图关闭测试伪造 `Current.Game` / `Game.World`，已编译的转发读取拿到不同对象：改为真实装配并恢复原 Game。
6. 袭击结算测试修改目标地图后仍期待原结算权限有效：明确验证身份变化撤销权限，恢复原身份后再测试正常结算。
7. 静态只读命令图标受先运行测试的空纹理替身污染：共用 UI 夹具在首次使用前初始化测试图标，保留非空图标断言。
8. **P1：第二名玩家加入世界死锁。** `ClientWorldGeneration` 持会话发布锁调用 Scribe 地形加载；1.7 的 `FastTileFinder` 并行任务进入殖民地点预留查询并等待同一锁。原生底图加载移出发布锁，每段仍复验会话、Game 和世界归属。实机线程栈：`entry-live-stacks.log`；新增并发用例修复前失败、修复后通过。
9. **P1：服务器存档重连死锁。** `PendingServerSessionSave.Load` 在发布锁内执行 `Game.LoadGame()`，触发同一并行地块校验环。改为只在 Game 切换和完成发布时持锁，反序列化使用捕获的 Game。线程栈：`reconnect-live-stacks.log`；新增并发加载用例修复前失败、修复后通过。
10. **P1：权威底图遗漏 feature 链接并残留本地 mutator。** `CopyRawByteArrays` 只扫描非公开字段，漏掉引擎公开的 `SurfaceLayer.tileFeature`；原生稀疏 mutator 解码只清理权威列表出现的地块，遗漏地块保留客端生成结果。改为同时复制公开数组，并在表层原始数据解码前清理已有 mutator。两端持久化 grid 差异证据为 `rimtest-A-persisted-grid.xml` / `rimtest-B-persisted-grid.xml`。新增用例修复前同时复现两种偏差，修复后四组世界测试及完整 228 组客户端测试通过。修复提交 `c9968888`，实机核心、全 DLC、50% 法语、100% 中文主机、高温自定义难度场景均已通过。
11. 雇佣兵场景参数名过时：使用实际入口的 `skill/level/days`，提交 `af698fd9`；核心和全 DLC 场景均验证真实雇用、扣款、技能等级与召回成功。`9ff02c09` 同时将地块 mutator 纳入完整摘要，并统一摘要与失败诊断的数据构造。
12. 银行逾期场景在本地读档清除银行缓存后直接还款，实际被“请先刷新银行状态”拦截。补充刷新并断言当前贷款身份，再验证少 1 银币拒绝和足额还款。提交 `487da83a`，无 DLC 与全 DLC 银行场景各 18 项全部通过，覆盖罚息、实际惩罚袭击、死亡债务、重连、精确偿还和护卫购买。

修复逐步测试并提交。前两轮实机运行因上述公共前置死锁中止，保留失败与未执行证据，未将重跑当作自动重试掩盖失败。

## 实机矩阵

完整矩阵证据为 `G:/RimTestRuns/ClashOfRim-full-regression-2026-10-19-final/20261019-151433-23e31eba/`。所有 36 场均已尝试，`notAttempted` 为空，进程退出码为 1。655 次计划调用中执行 591 次（549 通过、42 失败），另有 64 次未执行。34 场共同底图断言失败、2 场进入世界受阻，因此原构建的 profile 级结果为 0/36 通过，不能将其中通过的业务用例写成整个 profile 通过。

逐场计划与 `cases + notRun` 已核对一致；72 份客户端 manifest 均为 `1.7.4980 rev60`，36 份输入记录的模组 DLL 哈希一致，和运行后包哈希相同。核对结果见本地 `verified-evidence.json`、`full-run-summary.json`、`combined-summary.json`。

| 原构建失败项 | 次数 | 结论 |
| --- | --- | --- |
| world-tile-data | 34 | 产品底图缺陷，已修复并在 9 组补测中通过 |
| mercenary-hire | 2 | 夹具参数名过时，已修复并补测通过 |
| account-liabilities-before-abandon | 2 | 调用同一过时雇佣兵夹具，修复后账户全链路通过 |
| bank-overdue-repayment | 2 | 读档后未刷新银行报价的夹具问题，修复后通过 |
| shared-world-entry | 2 | 飞船配置的有效分辨率过小，未解决 |

### 原构建逐场结果

| Profile | 通过 | 失败 | 未执行 | 失败项 |
| --- | ---: | ---: | ---: | --- |
| stability-support-returns | 20 | 1 | 0 | world-tile-data |
| core-features | 36 | 2 | 5 | world-tile-data, mercenary-hire |
| coverage50-french | 13 | 1 | 0 | world-tile-data |
| coverage100-chinese-host | 13 | 1 | 0 | world-tile-data |
| dry-sparse-german | 13 | 1 | 0 | world-tile-data |
| wet-crowded-turkish | 13 | 1 | 0 | world-tile-data |
| cold-extreme | 13 | 1 | 0 | world-tile-data |
| hot-custom-comma | 13 | 1 | 0 | world-tile-data |
| dlc-cultures | 36 | 2 | 5 | world-tile-data, mercenary-hire |
| mod-storyteller100 | 13 | 1 | 0 | world-tile-data |
| dlc-culture-stress | 13 | 1 | 0 | world-tile-data |
| gift-repeat-accept | 17 | 1 | 0 | world-tile-data |
| stability-transfers-core | 21 | 1 | 0 | world-tile-data |
| stability-transfers-dlc | 21 | 1 | 0 | world-tile-data |
| stability-shop-core | 14 | 1 | 0 | world-tile-data |
| stability-shop-dlc | 14 | 1 | 0 | world-tile-data |
| stability-shop-stress | 14 | 1 | 0 | world-tile-data |
| stability-items-core | 16 | 1 | 0 | world-tile-data |
| stability-items-dlc | 16 | 1 | 0 | world-tile-data |
| stability-support-permanent | 16 | 1 | 0 | world-tile-data |
| stability-support-death | 16 | 1 | 0 | world-tile-data |
| stability-shop-regression | 14 | 1 | 0 | world-tile-data |
| bank-core | 11 | 2 | 5 | world-tile-data, bank-overdue-repayment |
| bank-all-dlc | 11 | 2 | 5 | world-tile-data, bank-overdue-repayment |
| relocation-core | 15 | 1 | 0 | world-tile-data |
| relocation-all-dlc | 15 | 1 | 0 | world-tile-data |
| gravship-odyssey | 1 | 1 | 14 | shared-world-entry |
| gravship-all-dlc | 1 | 1 | 14 | shared-world-entry |
| relocation-royalty | 15 | 1 | 0 | world-tile-data |
| relocation-ideology | 15 | 1 | 0 | world-tile-data |
| relocation-biotech | 15 | 1 | 0 | world-tile-data |
| relocation-anomaly | 15 | 1 | 0 | world-tile-data |
| account-core | 12 | 2 | 8 | world-tile-data, account-liabilities-before-abandon |
| account-all-dlc | 12 | 2 | 8 | world-tile-data, account-liabilities-before-abandon |
| offline-delivery-core | 18 | 1 | 0 | world-tile-data |
| offline-delivery-all-dlc | 18 | 1 | 0 | world-tile-data |
| 合计 | **549** | **42** | **64** | |

### 修复版本定向验证（已完成）

| 场景 | 通过 / 计划 |
| --- | --- |
| 核心功能、全 DLC 核心功能 | 86 / 86 |
| 50% 法语、100% 中文主机、高温自定义难度 | 42 / 42 |
| 无 DLC、全 DLC 账户废弃与重建 | 44 / 44 |
| 无 DLC、全 DLC 银行专项 | 36 / 36 |
| 合计 9 个 profile | **208 / 208** |

前 7 个通过场景见 `G:/RimTestRuns/ClashOfRim-full-regression-2026-10-19-followup/20261019-160042-1e52845a/`。该轮也保留两个银行旧测试失败及其 10 项未执行；修正后两组银行的完整通过证据见 `G:/RimTestRuns/ClashOfRim-full-regression-2026-10-19-bank-followup/20261019-163248-0c2ca945/`。以上是明确选择的修复后验证，不代表修复后的整个 36-profile 矩阵已重跑。

### 完整商品目录与压力结果

基础版完整目录 **397/397**、全 DLC 完整目录 **853/853** 均完成逐项上架和购买，失败项为 0。复杂物品的取消退款、交易投递，以及永久支援状态检查也通过；各 profile 仍保留原构建的底图摘要失败。

| 商店记录数 | 三次完整刷新（毫秒） | 实际客户端购买（毫秒） | SQLite 重开（毫秒） | 两客户端工作集（GiB） |
| --- | --- | --- | --- | --- |
| 1,000 | 289 / 286 / 280 | 2,397 | 123 | 1.36 / 1.57 |
| 10,000 | 657 / 639 / 536 | 2,541 | 179 | 1.57 / 1.49 |
| 50,000 | 2,677 / 2,778 / 2,901 | 3,959 | 510 | 2.38 / 2.97 |

每档确认完整列表到达、一次真实授权购买的库存与银币变化、64 次注册表并发/重放请求中 20 次独立成功，以及重开后的列表和买家历史一致。并发部分为注册表夹具，不代表 64 个真实客户端同时交易。该场景没有延迟通过阈值，以上为本机测量，不能据此宣称满足性能服务目标。原始数据为完整矩阵 `stability-shop-stress/20261019-164511-0859cad4/shop-stress.json`。

### 遗留问题与结果边界

- **飞船专项受显示条件阻断。** `gravship-odyssey` 和 `gravship-all-dlc` 在 `shared-world-entry` 收到 `Resolution too small (859x615), resetting UI scale to 1.`，飞船新地图与任务地点业务均未执行。另以固定 `-WindowSize=1600,900`、再补齐 DPI 迁移标记和关闭实例内系统缩放两种配置补测，均复现同一启动错误，未放宽日志断言。证据为 `ClashOfRim-full-regression-2026-10-19-gravship-followup/20261019-170302-40a95122` 和 `ClashOfRim-full-regression-2026-10-19-gravship-dpi/20261019-170456-b94f4511`。这些显示配置实验只存在于本地隔离运行脚本，没有修改系统桌面设置或提交为已验证的框架修复；飞船仍须在满足渲染尺寸要求的环境补测。
- `world-tile-data` 在原构建多个配置重复失败，已定位为上述底图完整性问题。原诊断只输出原始标量，未包含摘要中的 feature、邻接、道路和河流；补充诊断复用同一个 `TileData`，输出实际摘要输入及原始值，同时补充 mutator 内容比较，不放宽断言。完整矩阵继续测试同一原构建，修复单独补测，不将旧构建失败覆盖成通过。
- 原构建的 `mercenary-hire` 使用拆分前的参数名，导致真实雇用尚未执行并阻断依赖用例。同一入口也使两组账户场景的 `account-liabilities-before-abandon` 失败。修正后的核心、全 DLC、银行与账户链路均已完整补测通过。
- **遗留诊断：Play 场景重建时的原生空引用。** 原生 stdout 出现 `CameraDriver.Awake → ApplyPositionToGameObject → TickManager.ForcePaused` 空引用；修复包的 `core-features` 和 `dlc-cultures` 仍有记录，不能认定已解决。证据见本地 `camera-stdout-findings.json`、`camera-stdout-followup.json` 及其引用的原始 stdout。当前堆栈不足以区分引擎自身行为与模组切换 Game 的时序问题，需要独立定位。
- **P2：日志覆盖缺口。** `ScenarioRuntimeDiagnostics` 读取 `runtime.logs`，上述原生 `Waylith.UnityCompat.UnityWorld.InvokeMessage` 异常没有进入 Verse 日志观察器，因而修复包的 `runtime-errors` 仍报告通过。需要补充引擎日志采集或运行后 stdout 校验；本次已人工检查并披露，未将它归入可忽略错误。Headless 的 `Resolution too small (0x0)` 是另一条已明确豁免的启动信息。业务断言通过不代表原生引擎日志无错误。

语义测试不能代替窗口布局、头像渲染、鼠标操作或所有第三方模组组合的验证。1.6 只验证冻结内容与版本隔离，没有重建或运行旧版游戏。
