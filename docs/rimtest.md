# ClashOfRim 实机自动化

当前参数矩阵、43 项完整业务场景、14 项世界场景、运行方法与未覆盖分支见 [模组功能与世界参数实机矩阵](rimtest-matrix.md)。下文保留基础设施说明及 2026-10-15 的历史验收证据；历史通过数不代表新矩阵结果。

RimTest 是独立的通用框架。本项目的专用命令放在 `Tools/ClashOfRim.RimTestBridge` 测试模组中，业务断言放在 `Tools/ClashOfRim.GameTests` 外部进程中。正式模组不依赖这两个项目，发布包也不包含它们。

## 运行

日常修改先跑相关单元测试；需要实机证据时，只跑对应的 RimTest 专项。全量实机矩阵由用户明确安排。`Test-RimTestMatrix.ps1` 必须指定 `-Scenario`，或显式传入 `-FullMatrix`；三方兼容矩阵同样要求 `-Profile` 或 `-FullMatrix`，省略选择不再默认执行全量。

需要 RimTest 私有子模块访问权限、本地 RimWorld 1.7、Harmony、.NET 8 运行时和客户端构建所需的 .NET 10 SDK。

```powershell
git submodule update --init RimTest
pwsh ./Tools/Test-Regression.ps1 -IncludeClient -RimWorldManagedDir 'E:/SteamLibrary/steamapps/common/RimWorld'
pwsh ./Tools/Test-RimTest.ps1 -RimWorldPath 'E:/SteamLibrary/steamapps/common/RimWorld' -HarmonyPath 'E:/SteamLibrary/steamapps/workshop/content/294100/2009463077' -Headless
```

脚本构建客户端、桥接模组和运行器，启动真实 Kestrel 服务端以及两个独立游戏进程。每次运行使用新数据库、新账户、独立游戏配置和随机回环端口；服务端关闭认证调试模式。客户端通过正常菜单注册账户、提交世界基线、选择地点和开始游戏，随后调用原有游戏/模组操作。失败返回非零退出码，并在 `finally` 中停止本次启动的游戏。

可用 `-RunsDirectory 'G:/RimTestRuns/ClashOfRim'` 将实机副本与证据放到空间充足的磁盘。默认 `LocalOnly/RimTest/<run>/report.json` 记录逐项结果与证据；`trace.jsonl` 增量记录命令调用；`clients.json` 指向客户端清单和日志；`Server/Data` 保留服务端数据库和快照，便于分析失败。证据含测试存档和内部状态，不应提交到 Git。游戏运行目录含安装资源的目录联接，不能直接递归删除。

## 存储与保留策略（2026-10-26）

此前每次启动均复制约半 GB 游戏根目录文件，场景矩阵与双客户端进一步放大占用；停止游戏后没有回收。后续启动改用同盘、按文件内容冻结的共享缓存，默认位于运行盘根目录的 `RimTestCache`，也可用环境变量 `RIMTEST_CACHE_DIRECTORY` 指定同盘路径。游戏更新后产生新缓存版本；每次运行仍有独立的模组副本和可写配置/存档。

- 成功：保留报告、日志、输入指纹、配置、存档、服务端数据库；立即回收游戏运行目录、临时目录、运行器与桥接副本。
- 失败或结果未明确的停止：同一缓存最多保留最近 3 个游戏现场，以及最近 3 份运行器/桥接副本，最长 7 天；超限只回收运行文件，仍保留诊断证据。时间上限在后续测试结束时检查。
- 缓存：保留正在使用/失败保留现场引用的版本，另留最近 2 个未使用版本。进程仍在运行或控制脚本中断、尚未完成登记的目录不自动回收。
- 历史：不扫描、不纳管、不删除旧测试目录，已有冗余由用户自行处理。报告和存档仍会累积，后续可按需手动归档。

通用机制与无游戏文件系统测试见 [RimTest 存储策略](../RimTest/docs/run-storage.md)。普通目录大小工具可能把硬链接重复计数，实际占用应结合磁盘可用空间判断。

本次验证：`RimTest/tools/Test-RunStorage.ps1` 35 项文件系统检查通过；`Tools/Test-RimTestStrategy.ps1` 9 项场景选择检查通过（使用合成消费者，不启动游戏）。真实游戏使用现有构建包执行 `RimTest.LiveSmoke` 消费者通过，原生日志无 ERROR/FATAL；`Stop-Game -Outcome Passed` 后运行目录仅剩约 17.2 MiB 证据，约 499.4 MiB 的游戏文件保留在可复用缓存中。证据位于 `LocalOnly/RimTestStorageSmoke/20261026-065214-3d006997`。`Test-Live.ps1` 的自动构建入口未成功，本次直接启动缓存副本并执行同一冒烟消费者验证；未运行全量业务回归。

## 断言原则

2026-10-17 的模块迁移验证发现，隔离启动可能出现原版 Steam API 不可用提示，其按钮顺序是 Quit/Ignore。测试不能把第一个按钮一律当作确认。当前场景助手通过 `clash.labels` 获取游戏当前语言的原始提示与按钮文本，对这一明确允许离线继续的提示选择 Ignore；其他意外 Quit 选择会终止场景并保留诊断。仍由通用 `dialogs.choose` 调用原按钮，不直接关闭窗口或伪造 Steam 状态。失败证据保留在 `G:/RimTestRuns/ClashOfRim-module-migration/20261017-015942-ea5887f2`，退出发生在连接功能验证之前。

- `clash.invoke` 返回 `invoked` 只代表调用了原方法；必须等待业务结果并检查库存、合同、事件、快照或地图状态。
- 对投放物资推进真实 tick，等待运输舱落地后统计库存；不能用请求成功代替到账。
- 快照、物品、pawn 和事件用稳定标识关联。运行时对象句柄随游戏切换失效，不应存入长期场景文件。
- 玩家操作使用原按钮回调、原对话框校验、原浮动菜单和原工作/路径机制。禁止在断言前直接改服务器账本或伪造成功状态。
- 使用 debug/fixture 修改时必须单独标记；当前双客户端场景使用正常开局物资和交易流程，关闭 unsafe。
- 真实游戏测试与现有服务端、存档、崩溃恢复测试共同组成覆盖证据；前者不能替代后者。

## 功能清单与覆盖边界

下表是功能验收清单，不是“全部通过”的声明。每次执行结果以报告为准；尚未实现的场景不能计入通过率。当前运行器明确输出 `fullCoverage: false`。

| 功能族 | 已编写的实机路径 | 尚需扩展的实机验收 |
| --- | --- | --- |
| 账户与建档 | 两账户注册、认证、世界基线、不同地点、初始快照、改密、错误密码、重连；服主/普通玩家废弃后用原账户重建，隔离旧债务、合同、交易和联盟 | Steam 认证、实机旧档强制回滚、废弃响应丢失时的客户端恢复 |
| 聊天 | 公共消息、私信、未读/已读 | 第三账户隐私隔离、历史分页、超长/非法输入 |
| 管理员 | 配置读取、修改、恢复、普通用户无法修改 | 踢人、封禁、维护模式、广播、管理员迁移 |
| 银行 | 放款到账、还款扣款、合同关闭、快照前进 | 逾期罚款、债务、破产、取消及重复请求 |
| 商店 | 原编辑器构造货品、上架、购买扣款与到账、库存递减、下架 | 回收商品、耗尽库存、多买家竞争、复杂物品 |
| 交易 | 原编辑器选物/条件、报价确认、撤单返还、空投成交、双方库存守恒 | 接单备忘录、远行队成交、超时退回、复杂物品/pawn、跨层 |
| 外交 | 请求结盟、接收方批准、取消联盟、宣战、关系变化 | 拒绝、冷却、和平、支援请求 |
| 世界与远程地图 | 世界标记、外观设置/跨客户端同步/恢复、盟友观察、关闭观察、远行队装载与行进 | 观察权限拒绝、观察沙盒隔离、地图恢复、多图层 |
| 赠送 | 远行队原 Gift 命令、物品选择、接收、双方库存 | 拒绝返还、强制赠送、运输舱、载具 |
| 支援 | 原 Support 命令、期限选择、接收、提前/拒收/到期返回、永久支援、复杂 pawn 状态、到期死亡结算及重复交付 | 尸体销毁、失踪、战斗中各时点断线 |
| 雇佣 | 技能与期限、付款、pawn 到场及技能、召回穿梭机、原装载命令、离场与服务端合同关闭 | 护卫队、到期、伤害/死亡罚款 |
| 袭击 | 守方离线、敌对关系、原远行队袭击菜单、战场基线、带原警告的结束战斗、守方快照结算 | 攻击/破坏/战利品、撤退、超时与恢复 |
| 成就与榜单 | 十笔真实交易解锁、重复上传不计数、同账户新殖民地重新累计、废弃后隐藏旧排名、定向查询与存读档；服务端另覆盖全部内置阈值及前十/并列排序 | 其余内置成就的完整实机触发链、奖杯建造和视觉交互 |
| 兼容与故障 | 现有可移植/网络套件覆盖部分协议及恢复 | DLC 组合、第三方存储/载具/特殊物品的实机矩阵、掉线和进程中断 |

## 专用接口

账户废弃专用桥提供 `clash.abandon.open`、`clash.abandon`、`clash.abandon.choose`，复用原确认窗口、十秒倒计时和提交动作；`clash.achievement.version` 用于等待成就刷新真正发布。账户专项和旧会话修复见 [生命周期报告](rimtest-account-lifecycle-2026-10-17.md)。

`clash.actions` 返回允许调用的原方法及参数类型。`clash.invoke` 按明确方法名/签名绑定参数，只接受基础类型、受限配置 DTO 和先前发现的对象引用。复杂对象不能随意注入。所有业务写操作只允许回环 HTTP 测试服务器。

观察：`clash.state`、`clash.world`、`clash.inventory`、`clash.records`、`clash.events`、`clash.bank`、`clash.admin`、`clash.remote`、`clash.support.assignments`、`clash.mercenaries`。

银行与搬家专项另提供 `clash.bank.quests/debts`、`clash.relocation`、`clash.gravship`、`clash.dlc.pawns`。银行截止日期/余额、死亡、搬家起点和飞船构造均为带 `acknowledgeFixture` 与 debug 能力门禁的显式夹具；扣款、处罚、迁居和飞行完成由真实业务路径产生。见 [专项报告与范围](rimtest-bank-relocation-2026-10-16.md)。

可通过 `Test-RimTestMatrix.ps1 -Scenario bank-core,bank-all-dlc,relocation-core,relocation-all-dlc,gravship-odyssey,gravship-all-dlc` 选择专项；PowerShell 调用时 `-Scenario` 接受字符串数组。另有 `relocation-royalty/ideology/biotech/anomaly` 单 DLC 配置。飞船依赖原生渲染过场，运行脚本会对 `stability=gravship` 自动使用 `NoWindow`（即使矩阵传入 `-Headless`）；其余场景沿用指定模式。

支援归还修改可单独选择 `-Scenario stability-support-returns`：复用已有提前归还、拒收、到期、死亡结算及复杂 pawn 状态断言，双客户端使用中文/英语和中文/法语区域格式，启用全 DLC。它保留公共建档、世界核对、存读档及重连前置/收尾，不执行其他经济、袭击或远程地图专项。每步先跑相关单元测试，需要实机证据时只选受影响的专项；全量矩阵由用户安排。

编辑与操作：`clash.trade.*`、`clash.gift.*`、`clash.shop.template`、`clash.support.confirmDuration`、`clash.support.return`、`clash.mercenary.recall`、`clash.remote.close`。这些入口保留原业务校验，测试代码必须处理拒绝和异步完成。

通用的新游戏页面、节点对话框、地图/世界对象选择、gizmo、浮动菜单、远行队路径和 tick 推进位于 RimTest。新增其他模组测试时，可以复用这些接口并注册自己的扩展，而不修改 RimTest 核心。

运行器现使用独立的 `RimTest.Testing`：命名客户端、用例结果、观察等待、关联异步操作、日志游标、失败诊断、脱敏轨迹及 JSON/JUnit 报告已从业务程序提取。精确 tick 步进按提交回执的操作 ID 等待完成；普通信件暂停恢复仍是本套件显式提供的策略。进程启动/失败清理由通用 `Invoke-GameGroup.ps1` 管理，服务端配置与业务断言留在此项目。

新报告将项目专属的 `scope`、`fullCoverage`、`coverageCatalog` 放入 `metadata`，根层保留 `passed`、`cases`、`notRun`，另增加 `operations`。`junit.xml` 将启动失败或未完成计划标为失败；CI 应使用最终报告通过状态作为退出码。通用 API 见 `RimTest/docs/testing.md`。

## 实测发现的问题

本轮实机流程暴露并修复了 Core 世界没有 Odyssey landmarks 时无法上传底图、首次底图上传缺少认证、暂停时网络主线程完成队列不推进、入图后访问已释放 GameInitData、交易撤单未通知卖家自动返还、存档省略 tile 0 导致定位失败等问题。

此外，跨存档 pawn 本地 ID 不能直接绑定到另一玩家同号 pawn；关系占位对象现在保留 ThingDef/PawnKindDef，避免把羁绊动物生成人类。相关字段沿客户端 DTO、服务端协议和事件载荷传递，并保留对旧载荷的读取能力。Core-only 雇佣兵使用模组自有运输 Def，避免引用只有 Royalty 提供的 Ship_Shuttle。

远程地图的清理标记仍然持久化，但不再生成无订阅者的游戏任务信号；合法 quest tag 保留，原 tag 列表不会被修改。地图是否位于太空根据实际定义/地形判断，不以 OrbitalDebris 管理器非空作为依据。

基础设施失败也保留为失败：本机 Harmony 3.0.0-rc.2 / MonoMod 与 .NET 10 的组合曾在启动时出现 CLR 0x80131506，尚未定位其根因。运行器不会自动把重试或崩溃视为通过。Headless 的唯一错误豁免是启动基线中精确匹配的 0×0 分辨率提示，其他游戏错误都会使最终检查失败。

选址还存在独立的产品问题：客户端用图邻接判定殖民地间距，服务端用几何估计，边界地点可能在客户端通过但被服务端拒绝。当前场景选择同时满足双方规则的地点，并断言两个地点都登记到权威世界配置；这不代表该边界问题已修复。

## 本轮验证证据（2026-10-15）
通用能力提取后的复验：

RimTest 提交 `8046a68` 已推送；Windows 与 Ubuntu CI 均通过：[运行 38690229557](https://github.com/AIRsLight/RimTest/actions/runs/38690229557)。

- `G:/RimTestRuns/ClashOfRim/20261015-055820-0a933f18/report.json`：共享 `RimTest.Testing` 运行器下 19/19 通过、0 项未执行，记录 10 次异步操作；双客户端由通用进程组启动并在结束后停止。JSON、JUnit 和退出状态一致。
- RimTest 可移植测试更新为 21/21；独立实机验证覆盖稳定选择器跨存档重载、失效引用拒绝，以及制作清单的原料、工作者、技能范围、搜索半径和实际产物。详细路径见 `RimTest/docs/validation.md`。
- 本轮首次双客户端启动 `G:/RimTestRuns/ClashOfRim/20261015-054741-25ef604b` 再次遇到 CLR 启动崩溃，保留为失败报告，19 项未执行；后续成功运行没有覆盖或改写该失败记录。该基础设施问题仍未解决。

- `G:/RimTestRuns/ClashOfRim/20261015-053002-01d877f3/report.json`：19 项通过、0 项失败、0 项未执行；真实双客户端与持久化服务端，RimWorld `1.7.4979 rev109`、Core + Harmony `3.0.0-rc.2`、Headless、unsafe 关闭。测试场景合计约 103 秒，不包含构建与启动时间。
- `LocalOnly/rimtest-final-regression.log`：`Test-Regression.ps1 -IncludeClient` 成功，包含事件、存档、协议/客户端核心、完整网络回归与本地游戏引用客户端测试。
- RimTest 本地可移植测试 17/17；修复监听端点重建竞争后，100 次连续重连在 Windows、Ubuntu CI 均通过，代码验证运行 `38686569867`、最终文档提交运行 `38686914628` 均成功。
- 独立通用框架的开局、语义操作、精确 tick 推进与远行队返回测试见 `RimTest/docs/validation.md`。

这不是全功能完成声明。最终日志门禁检查 error、bridge、probe 及指定业务失败 warning；其余 warning 保留在报告中。当前仍出现存档引用未解析、意识形态回退、保存期间分配 ID 等提示，尚不能据此宣称 pawn 关系、思绪或全部投影数据无损。新关系元数据字段在本轮同版本客户端/服务端之间通过验证，混合旧版本互通尚未验收。

## 2026-10-16 稳定性专项

新增的袭击重载与重复结算、远程地图隔离、pawn 往返/永久/死亡、复杂物品、完整商店目录及 50,000 商品压力测试，见 [稳定性报告](rimtest-stability-2026-10-16.md)。测试目录保留普通业务、世界参数和专项分组；无需每次改动都运行全部完整商品购买。

快速复验已复现的商店问题可选 `-Scenario stability-shop-regression`；完整目录使用 `stability-shop-core,stability-shop-dlc`，大数据使用 `stability-shop-stress`。六种商品的快速样本不替代完整目录。
