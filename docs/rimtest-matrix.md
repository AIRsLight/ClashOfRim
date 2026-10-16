# 模组功能与世界参数实机矩阵

场景源为 `Tools/RimTestScenarios.json`，执行器为 `Tools/ClashOfRim.GameTests`，模组专用观察与原操作入口为 `Tools/ClashOfRim.RimTestBridge`。这是可执行验收目录，不是所有组合已通过的声明。结果必须看本次 `matrix.json`、各场景的 `report.json` / `junit.xml`，失败和未执行项均不算通过。

当前目录包含 70 个独立用例 ID、33 个参数场景，完整执行计划为 592 次用例调用：原 11 场 214 次、10 场流转/商店专项 172 次、10 场银行/迁居/DLC 专项 162 次、2 场账户生命周期专项 44 次。这是目录计划，不代表同一构建已完整执行。夹具边界和压力实测见 [稳定性报告](rimtest-stability-2026-10-16.md)，账户专项见 [生命周期报告](rimtest-account-lifecycle-2026-10-17.md)。完整商品目录包含数百次逐项上架及购买，耗时明显高于普通业务场景。依赖失败时实际执行数会减少，报告保留 `notRun`。

## 执行与证据

```powershell
pwsh ./Tools/Test-RimTestMatrix.ps1 `
  -RimWorldPath 'E:/SteamLibrary/steamapps/common/RimWorld' `
  -HarmonyPath 'E:/SteamLibrary/steamapps/workshop/content/294100/2009463077' `
  -RunsDirectory 'G:/RimTestRuns/ClashMatrix' -Headless

# 修改后只复验相关场景；完整验收不要带此筛选。
pwsh ./Tools/Test-RimTestMatrix.ps1 `
  -RimWorldPath 'E:/SteamLibrary/steamapps/common/RimWorld' `
  -HarmonyPath 'E:/SteamLibrary/steamapps/workshop/content/294100/2009463077' `
  -Headless -Scenario core-features,dlc-cultures
```

每场使用新数据库、新账户、独立存档和两个真实游戏进程。游戏从原建档流程开始；服务端使用真实认证与持久化。场景间继续运行，场景内有依赖的业务失败会阻断后续步骤并列入 `notRun`。独立世界观察失败会记录并继续其他观察；不会把异常吞掉后标成通过。无自动重试直到变绿。

省略 `-Headless` 可运行保留渲染的 `-NoWindow` 对照；这仍是语义接口验收，不能据此证明头像、布局或鼠标交互正确。单独修改场景时，将 JSON 保存到文件并传给 `Test-RimTest.ps1 -Profile <文件>`，报告会复制该配置并记录哈希。

`inputs.json` 保存仓库版本、工作区状态和实际二进制 SHA256；客户端清单保存运行时、语言、区域格式和 DLC。`trace.jsonl`、游戏 stdout、数据库和快照留在每次唯一的证据目录。不要递归删除含游戏资源目录联接的运行目录，不要提交游戏资源、测试存档或日志。`-SkipBuild` 仅供主动复用已构建版本，不用于证明源码对应的构建已通过。

## 参数组合

| 场景 ID | 覆盖率 | 叙事者 / 难度 | 语言与区域格式 | 额外条件 |
| --- | --- | --- | --- | --- |
| core-features | 30% | Cassandra / Peaceful | 英文 en-US → 英文 en-US | 全部已实现业务用例 |
| coverage50-french | 50% | Phoebe / Easy | 英文 en-US → 法文 fr-FR | Unicode / 逗号种子 |
| coverage100-chinese-host | 100% | Randy / Medium | 中文 zh-CN → 英文 en-US | 大世界、Unicode 种子 |
| dry-sparse-german | 30% | Randy / Rough | 英文 en-US → 德文 de-DE | 最低降雨、最低人口 |
| wet-crowded-turkish | 50% | Cassandra / Hard | 英文 tr-TR → 中文 en-US | 最高降雨、最高人口；土耳其大小写规则 |
| cold-extreme | 30% | Phoebe / Extreme | 英文 en-US → 英文 en-US | 最低温度 |
| hot-custom-comma | 100% | Cassandra / Custom | 法文 fr-FR → 英文 de-DE | 最高温度；1.37、0.17、0.63 自定义数值 |
| dlc-cultures | 30% | Cassandra / Peaceful | 中文 zh-CN → 英文 en-US | 五个 DLC、污染 0.25、最高地标密度、全部业务用例 |
| mod-storyteller100 | 100% | 测试模组定义 / Custom | 英文 en-US → 英文 en-US | 验证不把叙事者限制为原版三种 |
| dlc-culture-stress | 30% | Cassandra / Peaceful | 中文 zh-CN → 英文 fr-FR | 与 DLC 功能场景对照，单独检查区域格式压力下的世界/存档 |
| gift-repeat-accept | 30% | Cassandra / Peaceful | 英文 en-US → 英文 en-US | 独立的 18 项礼物重复接受场景；复用联盟、远行队和赠送路径 |

测试叙事者定义只在测试桥接包中，不进入正式发布。它验证 Def 加载和传输，不能代表所有 Workshop 叙事者的兼容性。游戏会重设线程区域格式，因此 `Culture` 是显式测试夹具：RimTest 在命令/游戏线程应用并用 `runtime.hello` 检查实际值，未改变操作系统区域设置。混合语言只通过产品允许继续的语言警告，不绕过内容不兼容检查。

这组配置覆盖三种正式世界覆盖率、所有原版难度、原版及外部叙事者、世界参数上下界、中文/英文/法文/德文和不同数字格式。它不是参数笛卡尔积，也不是每个 DLC 子集的穷举。

## 每场共有的世界与同步断言（14 项）

| 用例 ID | 检查内容 |
| --- | --- |
| launch-profile | 实际语言、Culture、DLC、unsafe 关闭 |
| shared-world-entry | 同一权威世界、不同合法殖民地、两份地点登记和首次快照 |
| world-parameters | 种子、覆盖率、雨量、温度、人口；DLC 地标与污染参数 |
| world-geometry | 每个图层、每个 tile 中心、完整数量；服务端分离底图实际存在且可解码 |
| world-tile-data | 全地块 biome、邻接、道路/河流、地形/气候/污染摘要；保留首个差异块的原始值 |
| world-feature-identities | 地区 ID、Def、坐标、大小；本地化名称存在；同语言名称一致 |
| storyteller-difficulty | 实际叙事者、难度 Def 及所有公开基础类型难度字段；防止静默回退 |
| culture-references | 意识形态 ID 唯一、pawn 引用可解析；共享 NPC 派系的 CultureDef / foundation / memes / precepts 一致 |
| world-persistence | 新服务容器从磁盘重建后，逐字段比较配置及底图字节 |
| storyteller-permission | 普通玩家打不开受限编辑页，难度不变 |
| runtime-difficulty-sync | 管理员原编辑页切到不同叙事者和自定义数值；在线玩家实际应用；恢复原值 |
| snapshot-save-reload | 保存并加载实际存档，验证世界、地块、难度、地区、pawn 及库存 |
| session-reconnect | 上传、登出、原登录流程重连；保持殖民地、世界和难度 |
| runtime-errors | 游戏 / bridge / probe 错误门禁及指定业务失败 warning |

几何坐标逐值精确比较。地块数值按游戏存档 ushort / byte 量化精度规范化后比较，避免把生成态浮点数与存档量化误差判成数据损坏；不是放宽成任意浮点容差。显示语言允许改变标签，不允许改变 canonical Def、世界拓扑、难度和 pawn 文化引用。

共享派系比较排除 `ClashOfRim_PlayerProxy`：它是随玩家会话变化的投影，不能当作生成时的 NPC 派系。未启用 Ideology 时不比较隐藏的 NPC 意识形态；启用 DLC 的场景仍逐项检查文化、信条和引用。存档重载和重连同时比较 WorldInfo、NPC 派系及完整世界摘要，避免仅凭世界 ID 相同放过损坏。

## 两个完整业务场景额外断言（28 项）

| 功能 | 用例 ID 与断言 |
| --- | --- |
| 账户与外观 | `account-password-change` 错误旧密码拒绝、改密、恢复；`colony-appearance-sync` 跨客户端同步和恢复 |
| 聊天 | `public-chat`、`private-chat-read`；`unicode-chat-blank-rejection` 精确中法土日韩/emoji 文本、一次投递、空白拒绝 |
| 管理员 | `admin-configuration-permissions`、`admin-action-permissions` 普通用户与自操作保护；`admin-role-grant-revoke`；`admin-broadcast` Unicode 持久广播及审计 |
| 会话管理 | `admin-kick-reconnect`；`admin-ban-unban` 封禁阻止登录、解除后可登录；`maintenance-password-rejection` 错密码及维护封锁、解除后恢复 |
| 银行 | `bank-input-rejection` 零、负数、极大额及非法期限不改库存/快照；`bank-loan-repayment` 放款到账、重复借款拒绝、还款扣款、重复还款不重复扣款 |
| 商店 | `shop-purchase-stock` 上架、购买、扣款/到账、库存、下架；`shop-buyback-stock-rejection` 回收、超库存拒绝、耗尽后重复购买无副作用 |
| 交易 | `trade-cancel-refund`；`trade-memo-cancel` 接单备忘不提前交货、撤销备忘不动卖家托管；`trade-drop-pod-fulfillment` 真实运输舱到账和双方守恒 |
| 成就 | `achievement-unlock-leaderboard` 十次真实成交触发 trades_10、单份权威聚合、他人榜单可见 |
| 外交/地图 | `alliance-request-accept`；`ally-observation-close` 原观察地图/关闭；`caravan-load-travel` 实际装载、原路径行进 |
| 赠送 | `caravan-gift-accept` 原远行队入口、物品守恒；`gift-repeat-accept` 在独立场景中检查重复接受和刷新不重复交付 |
| 支援 | `support-send-early-return` 原期限页、接收、实际 pawn 身份/文化/引用完整、提前返回 |
| 雇佣 | `mercenary-hire` 付款、到场、技能；`mercenary-recall` 原装载、穿梭机离场、权威合同关闭 |
| 袭击 | `raid-open-settle` 解盟、宣战、守方离线、正确战场快照、原结束警告、守方结算快照、战场清理 |

不以请求 `invoked` / HTTP 成功代替验收。库存必须经历实际 tick / 运输舱落地，快照、事件、合同和服务器状态必须有相应断言。原方法调用集中在桥接白名单，参数化场景和等待/库存/比较逻辑集中复用，避免各语言各写一份业务流程。

重复礼物已经复现了额外物资，因此将它移到独立场景，防止污染主功能链路后续的支援、雇佣、袭击检查。它仍在默认矩阵中运行、仍要求库存不变，失败照常使矩阵失败；没有删除断言或标记为预期通过。

登录等待同时要求入口实际启动、认证结束和新的游戏 epoch。认证成功早于存档替换，不能只观察“已在线 + 有地图”。初次进入和恢复快照共用同一夹具：推进 600 tick 让运输舱落地，再征召殖民者固定经济断言的库存；初始权威快照可能还包含未打开的开局运输舱。该推进是真实游戏模拟，不直接写库存。

退出通过 RimTest 的 `game.returnToMenu` 调用原版 `GenScene.GoToMainMenu`，因此正常触发模组登出钩子并卸载游戏。上传完成后才允许丢弃当前本地场景，并等待菜单就绪。仅调用模组退出钩子而继续保留游戏组件运行会触发自动重连，不能用来模拟“守方离线”。

## 尚未覆盖的实机分支

下列是明确的后续验收项，当前 `fullCoverage` 保持 false，不能据此声称“全部特性通过”。已有服务端/存档回归应同时运行 `Test-Regression.ps1 -IncludeClient`，但它们不替代这些实机交互。

| 分支 | 所需可重复场景与关键断言 |
| --- | --- |
| Steam 身份 | 两个真实授权 Steam 用户；票据失效、重放和替换会话不污染新会话 |
| 重力飞船 / Issue #2 | Odyssey 重力飞船开局；100% 覆盖率普通飞行和太空跃迁；飞行前后殖民地锚点、跨层标记和重连快照一致 |
| DLC 子集 / Workshop | 独立启停每个 DLC；指定并固定第三方载具/存储/叙事者版本；不兼容清单必须拒绝 |
| 银行违约 / 雇佣伤亡 | 正常到期、取消、罚款、破产、护卫队消耗；受控时钟/伤亡夹具必须与无夹具场景区分 |
| 复杂货物 / 支援 | 质量、耐久、容器、基因、动物/关系、装备；拒收返还、永久/到期支援和死亡；全图层交付 |
| 运输路径 | 原运输舱赠送、强制赠送、载具与远行队成交；目的地地图卸载后的恢复 |
| 袭击损失 / 观察隔离 | 实际攻击、战利品/建筑/地面变化；越权结算拒绝；观察不改原殖民地；撤退、过期和重试 |
| 故障注入 / 运维 | 在上传/确认/通知阶段断网、终止进程、重启服务器；离线/仅在线通知差异；回档防护、废弃殖民地、删档和管理员迁移 |
| 多用户竞争 | 第三个玩家并发购买、私信隐私、重复接受；与现有服务端竞争/崩溃矩阵互证 |

最新实测问题及复现路径见 [参数与跨语言发现](rimtest-findings-2026-10-16.md)。旧的 19 项结果只说明旧套件通过，不能用于本矩阵的通过率。

## 2026-10-16 实测记录

环境为本地 Steam `1.7.4980 rev60`。机器可读汇总见 [逐场结果](rimtest-results-2026-10-16.json)，其中保存原报告路径、用例状态及实际二进制哈希。它汇集迭代中的各场报告，并非一次固定版本的全绿运行；工作区还有其他未提交改动，原 `inputs.json` 保留了对应状态。

| 场景 | 通过 / 失败 / 未执行 | 结果说明 |
| --- | --- | --- |
| core-features | 22 / 1 / 19 | 真实退出菜单后残留登录/上传忙碌状态，维护测试被阻断 |
| coverage50-french | 1 / 1 / 12 | fr-FR 夹具下原生语法规则解析 `0.15` 失败 |
| coverage100-chinese-host | 1 / 1 / 12 | 地区文字初始化空引用，建档阻断 |
| dry-sparse-german | 1 / 1 / 12 | 建档完成回调空引用 |
| wet-crowded-turkish | 1 / 1 / 12 | 建档完成回调空引用 |
| cold-extreme | 14 / 0 / 0 | 较早迭代通过；退出仍使用旧钩子夹具，不能证明最新真实退场重连通过 |
| hot-custom-comma | 1 / 1 / 12 | fr-FR 夹具的基础游戏解析错误；早一轮另捕获难度数值放大 |
| dlc-cultures | 1 / 1 / 40 | 地区文字初始化空引用；早一轮另捕获文化目录元数据丢失 |
| mod-storyteller100 | 12 / 1 / 1 | 全量世界/难度/存档检查通过，真实退场后重连阻断 |
| dlc-culture-stress | 1 / 1 / 12 | 建档空引用；早一轮另捕获存档数值放大和网格变化 |
| gift-repeat-accept | 14 / 1 / 3 | 独立复现重复接受同一礼物，木材 305 → 310 |

上述选取报告共 69 项通过、10 项失败、135 项因依赖未执行，不能换算成全部功能通过率。所有旧失败都保留。业务链较早运行 `G:/RimTestRuns/VerifiedCore/20261016-101925-1a4a9513` 已逐项通过银行、商店、交易、十次成交成就、联盟、观察、远行队、礼物、支援、雇佣/召回；随后在旧退出夹具下袭击遇到守方自动重连导致快照变化。更早的 `ExpandedCore/20261016-100305-b8add63c` 通过了正常袭击结算，但也不能替代最新退出路径验证。

可移植和独立验证：`Test-Regression.ps1 -IncludeClient` 通过；银行修复后的客户端测试通过；RimTest 40/40 通过；包含真实返回菜单的独立 live smoke 通过；冻结 1.6 的 77 个文件哈希通过。测试驱动并分别提交了语言请求头编码、世界底图就绪通知时机、重复借款前置拒绝三项产品修复。当前实机矩阵保留红项，未把它们标成预期通过。

## 2026-10-16 修复后复测

上节是修复前的历史结果，保持原样。最新结果见 [修复验收 JSON](rimtest-repair-results-2026-10-16.json) 与 [修复原因和提交记录](rimtest-findings-2026-10-16.md#修复验收与提交)。

| 场景 | 最新通过 / 失败 / 未执行 |
| --- | --- |
| core-features | 42 / 0 / 0 |
| coverage50-french | 14 / 0 / 0 |
| coverage100-chinese-host | 14 / 0 / 0 |
| dry-sparse-german | 14 / 0 / 0 |
| wet-crowded-turkish | 14 / 0 / 0 |
| cold-extreme | 14 / 0 / 0 |
| hot-custom-comma | 14 / 0 / 0 |
| dlc-cultures | 42 / 0 / 0 |
| mod-storyteller100 | 14 / 0 / 0 |
| dlc-culture-stress | 14 / 0 / 0 |
| gift-repeat-accept | 18 / 0 / 0 |

合计 214 次通过、43 个不同用例。证据由一次完整矩阵的九个未受后续 DLC 修复影响的场景，以及最终构建的两个 DLC 场景组成；各批内二进制清单一致，批间不同。完整矩阵原本为 213/214，所有中间失败仍保留，不称作一次固定构建全绿。

最终构建曾有一次用例开始前的 CLR/MonoMod 启动崩溃（42 项未执行），同构建重跑完整 DLC 场景通过；启动异常根因仍待定位。语义用例通过不代表覆盖上节列出的所有实机分支，`fullCoverage` 保持 false。

## 银行、欠款、迁居与 DLC 专项（2026-10-16）

新增 10 个配置，当前场景目录合计 31 个配置。此次专项选取 10 次完整通过运行，共 162 次用例调用、21 个不同用例；不是一次固定构建全绿，也不替代前面历史测试。覆盖贷款/逾期/佣兵死亡欠款、远行队和任务地点迁居、飞船新地图与已有地图落地，以及单 DLC/全 DLC 状态保存。

实测修复了旧债务记录重复扣款、任务地图缺少迁居接管、旧地图清理跨线程三项问题。详情与未覆盖范围见 [专项报告](rimtest-bank-relocation-2026-10-16.md)，报告/构建哈希及失败记录见 [验收 JSON](rimtest-bank-relocation-results-2026-10-16.json)。

## 账户生命周期与成就专项（2026-10-17）

`account-core` 与 `account-all-dlc` 各计划 22 项（14 项共有检查及 8 项账户业务），覆盖原账户注册、改密、服主/普通玩家废弃、重建、负债与合同清理、另一玩家隔离、全局商店/账户权限保留、两代交易成就、榜单与持久化。全 DLC 使用中文 zh-CN 和英文 fr-FR。

新增服务端回归覆盖 11 个内置成就阈值、四种区域格式、累计事件去重、前十/并列排序及历史隔离，并修复废弃后旧在线会话可保活而阻止新登录的问题。场景边界、启动环境限制和实测证据见 [账户生命周期报告](rimtest-account-lifecycle-2026-10-17.md)。

最终两场均 22/22 通过，共 44 次；全 DLC 使用临时 `DOTNET_PROCESSOR_COUNT=4` 规避本轮启动阶段的 CLR/MonoMod 崩溃，不能据此宣称默认线程下启动稳定。服务端 Save 回归 135/135 通过。精确报告和中间失败见 [验收 JSON](rimtest-account-lifecycle-results-2026-10-17.json)。
