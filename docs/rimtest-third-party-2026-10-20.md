# 第三方兼容模块实机验证（2026-10-20）

本轮在本地 Steam RimWorld `1.7.4980 rev60` 中实际加载六个兼容目标，用原模组对象和双客户端业务触发兼容效果。测试为专项，没有运行常规全量矩阵。运行从主仓库 `f308a487` 和当时已有的未提交工作区开始；每场 `inputs.json` 记录实际源码状态、配置和 DLL 哈希，不能将其理解为纯净提交构建。

最终六组启动配置 18/18 用例通过，三组业务配置分别为 19/19、21/21、21/21，共 61/61 通过。确认并修复载具默认防御 AI 问题，同时修正 RimTest 远行队窗口生命周期。VEF 经启停对照确认保留原策略。

## 环境与范围

使用隔离的游戏目录、配置、存档、账户和 SQLite 服务器；未覆盖 Steam 模组或个人存档。`DOTNET_PROCESSOR_COUNT=4`，Headless；A 为英文/en-US，B 为英文/fr-FR。pawn 场景启用五个 DLC，地图场景为 Core。1.6 冻结文件未改动，哈希校验通过。

| 兼容目标 | Workshop ID | 本地元数据声明 | 实际触发的兼容效果 |
| --- | --- | --- | --- |
| Humanoid Alien Races | 839005762 | 支持 1.7 | Ratkin 雕像原生外观元数据，撤单返还、跨玩家交易、接收方存读档 |
| Facial Animation | 1635901197 | 最高 1.6 | 人类和 Ratkin 雕像的面部状态，接收后的显示 pawn 控制器 Def |
| Vanilla Expanded Framework | 2023507013 | 支持 1.7 | MVCF 未启用时不创建管理器；启用后支援 pawn 恢复刷新，检查非空 verb 集合和 pawn 归属 |
| Adaptive Storage Framework | 3033901359 | 最高 1.6 | Neat Storage 容器四堆共 300 钢，在远程投影中保持容器关系、数量和禁止搬运状态 |
| Vehicle Framework | 3014915404 | 最高 1.6 | Mule 乘员与 17 钢货物投影、防御点原生登车开关、分配人员登车、守方阵营、结算后载具与货物守恒 |
| Melee Animation | 2944488802 | 最高 1.6 | 创建真实双人长剑决斗 renderer；投影清除临时动画，源地图保持不变 |

内容依赖包括 Ratkin `1578693166`、Neat Storage `3416243474`、Vanilla Vehicles Expanded `3014906877`。后者同样只声明到 1.6。本轮没有修改第三方版本声明或替换其二进制；这些版本能通过本次测试不等于它们已正式支持所有 1.7 行为。

## 复现与修复

1. **载具误用普通防御 AI。** 远程袭击准备先对载具分配普通防御 Lord 并启动 think-tree，然后才执行载具兼容回调。实机出现 `Mule started 10 jobs in one tick` / `IdleVehicle` 递归任务错误。主模组新增由兼容模块注册的默认 AI 筛选接口，同时供普通远程居民和防御点使用；Vehicle Framework 排除自身载具，继续由其兼容流程处理。单元测试覆盖缺省允许、按对象筛选、重复注册、异常隔离和撤销；实机断言载具无普通 Lord/duty，人员登车及阵营正常，并运行最终日志门禁。
2. **纠正 MVCF 空缓存的误判。** 只加载 VEF 时，MVCF 原生日志显示安装 0 项功能补丁，此时收到的 pawn 没有管理器是有效状态。最初误将其视为缺陷，追加了无条件创建；无装备样本通过，但增加武器后出现 `Attempted to get ManagedVerb ... which does not have one`，否定了该改动，现已撤回。测试分为未启用（不创建管理器）和启用（非空 verb、归属正确、原生初始化来自兼容回调）两个对照。启用场加载独立测试包 `AIRsLight.RimTest.MvcfEquipmentFixture`，通过原生 `MVCF.ModDef` 声明 `ExtraEquipmentVerbs`；原生启动日志确认应用 33 项补丁。该夹具只有功能声明，不实现或替换任何第三方缓存/verb 算法。观察器只读弱引用表，不为检查创建管理器；Harmony 探针只记录初始化调用栈。最终保留原兼容策略。
3. **RimTest 远行队遗漏原窗口生命周期。** Vehicle Framework 在 `Dialog_FormCaravan.PostOpen` 初始化 formation，原测试框架直接提交未打开的模型，造成空引用。框架现以原生窗口生命周期构造提交模型，继续使用原校验和警告确认；异常时关闭窗口。准备/预览仍无窗口。已单独提交 RimTest `1836ba0`。

**雕像原生对照：** Ratkin 和人类雕像的显示 pawn 发色在普通保存/重载时也会变化。原生对照已经实际观察到这一点；测试只排除这个临时显示字段的严格相等比较，仍完整比较 `CompStatue` 持久化 XML（包含保存的发色、HAR 色彩通道、FA 字段）、种族、体型、头型、发型、肤色、附件变体和面部控制器 Def。`taleRef` 是世界内引用，不作为跨玩家外观相等项。Headless 不触发 FA 肖像渲染初始化，因此夹具通过其原生 `InitializeIfNeed()` 初始化真实控制器，然后调用原生生成雕像流程；没有手工注入兼容元数据。

## 执行结果与证据

证据根目录：`G:/RimTestRuns/third-party-compat-2026-10-20`。每场保留 JSON/JUnit、完整命令 trace、独立游戏日志、实际输入哈希和测试存档；失败运行未覆盖。

| 运行 | 结果 | 相对证据目录 |
| --- | --- | --- |
| 六个独立模组启动配置 | 6/6 配置、18/18 用例通过 | `startup-20261019-175715` |
| 地图、载具与动画 | 19/19 通过 | `maps/20261019-182024-aade4bc5` |
| 雕像与 MVCF 未启用对照 | 21/21 通过 | `pawns/20261019-182848-f487f0d4` |
| 雕像与 MVCF 启用对照 | 21/21 通过；实际 1 个 verb、0 个 null，manager/verb 归属一致，兼容初始化探针命中 | `pawns/20261019-183439-fc35f465` |
| 客户端相关单元测试 | 2/2 组通过，70 个桥接命令合同 | `D:/Projects/ClashOfRim/LocalOnly/third-party-compat-2026-10-20/client-final.log` |
| 场景目录和袭击编排单元测试 | 3/3 组通过 | 同目录 `save-final.log` |
| RimTest 可移植单元测试 | 41/41 通过 | 同目录 `rimtest-unit.log` |

保留的重要失败：`maps/20261019-181026-dcfe2727` 为真实载具 AI 错误；`pawns/20261019-181646-1fae18d7` 将未启用 MVCF 的空缓存误判为失败；其后无装备样本 `pawns/20261019-181949-5d245ea5` 虽 21/21 通过，但 verb 集合为空，不足以证明恢复正确；增加武器的 `pawns/20261019-182349-3ea716a3` 揭示无条件创建造成原生错误，这项实验修复已撤回。`maps/20261019-181713-cf64b923` 是新增观察断言错误地使用必填字符串读取 null，已改用 JSON null 判断。早期雕像夹具没有初始化 FA 原生控制器，后补充了初始化和原生重载对照。

`pawns/20261019-182816-d2c542c6` 在游戏中途通过 MVCF 原生调试入口启用功能，已创建的 pawn/verb 缺少初始化并产生 null verb 与原生异常；这是无效的启用夹具，不能据此认定跨玩家流转有问题。最终改为启动时声明功能，并让观察器保留 `nullVerbs` 计数、将任何空 verb 判为归属失败，不过滤掉坏数据。

`pawns/20261019-183142-b7b3960d` 在撤单后仅观察到一尊已生成雕像；原观察遗漏 pawn 搬运中的物品，且新生成的外观样本 pawn 未征召，可能参与搬运。随后补全搬运观察、报告缺失显示 pawn 的雕像而不滤掉它，并征召夹具样本以稳定经济断言。最终完整场景未再复现数量不足；该早期失败不能直接归因为物品丢失。

RimTest 通用 `Test-Live.ps1` 的运行 `framework/20261019-181051-403e901d` 因远程桌面 `Resolution too small (859x615)` 日志门禁失败，未算作通过；修改涉及的远行队行为由本次启用 Vehicle Framework 的专项实机覆盖。Headless 测试只允许既有的精确 `0x0` 启动分辨率提示，不豁免其他错误。

服务端日志仍保留断连时 WebSocket 未完成关闭握手的异常，以及 pawn 场景重连时世界配置 POST 的 HTTP 400；本轮没有修改这两条服务端路径。业务终态与客户端错误门禁通过不等于所有日志都没有警告。

产品修复提交：主仓库 `3bbfd5e4`（载具默认 AI，兼容子模块 `de376bf`）。中间提交 `8b25415e` / 兼容子模块 `11a94a4` 的 MVCF 创建实验由 `9881a3e5` / `54dc6ea` 撤回，历史保留。

## 再执行

```powershell
$env:DOTNET_PROCESSOR_COUNT = '4'
$paths = @{
    RimWorldPath = 'E:/SteamLibrary/steamapps/common/RimWorld'
    HarmonyPath = 'E:/SteamLibrary/steamapps/workshop/content/294100/2009463077'
    WorkshopRoot = 'E:/SteamLibrary/steamapps/workshop/content/294100'
}
# 六组独立加载测试，不代表业务效果验证。
./Tools/Test-RimTestThirdParty.ps1 @paths -RunsDirectory 'G:/RimTestRuns/ThirdPartyStartup'
# 三组实际业务配置；按改动选择其中一组。
./Tools/Test-RimTest.ps1 @paths -Profile Tools/RimTestThirdPartyMaps.json -RunsDirectory 'G:/RimTestRuns/ThirdPartyMaps' -Headless
./Tools/Test-RimTest.ps1 @paths -Profile Tools/RimTestThirdPartyPawns.json -RunsDirectory 'G:/RimTestRuns/ThirdPartyPawns' -Headless
./Tools/Test-RimTest.ps1 @paths -Profile Tools/RimTestThirdPartyMvcf.json -RunsDirectory 'G:/RimTestRuns/ThirdPartyMvcf' -Headless
```

包按 Package ID 从 Workshop 根目录解析，缺失或存在重复匹配会明确失败。地图配置自动构建并装入两项服务器插件，并从真实 `/server/plugins` 接口核实加载；源码构建和既有场景编排保持共用。三项业务配置不加入常规 37 配置全量矩阵，避免让不安装第三方模组的环境隐式依赖 Workshop 内容。

## 尚未覆盖

本轮覆盖每个兼容模块的实际效果样本，未穷举兼容包所有分支。飞行载具抵达/拒绝、攻击方载具远行队入图与突击、载具部件损伤和货物损失结算、VEF 派系发现/KCSG 特殊事件、每种第三方武器 verb、雕像商店/赠送入口、不同物品容器组合仍需各自专项。Headless 结果不证明肖像、动画画面、声音或鼠标交互的视觉正确性。用户未要求全量，故本轮未扩展为完整组合回归。

## 后续提交前复核：关系占位对象的种族

2026-10-20 后续复核时，`ThirdPartyCompat` 工作区已干净，`54dc6ea` 及载具 AI 修复已在远端。主仓库还有未提交的通用 pawn 关系修复，本节记录这组改动；不将上面的实机结果算作本组新增逻辑的实机证明。

关系载荷新增可选 `OtherPawnThingDef`、`OtherPawnKindDef`，沿客户端采集、服务端安全包／协议映射、礼物接收、支援接收与返回完整保留。恢复缺席的关系对象时按种族选择 PawnKind，强制生成新占位对象并关闭原版随机关系生成，避免一律生成普通人类或复用另一名世界 pawn。没有加入 HAR 专用类型依赖或硬编码种族名；所有物种共用正式恢复流程。

审查新增的边界测试先复现：声明了未加载的种族但携带另一个有效 PawnKind 时，原待提交实现会接受错误种族。另一个分支是从旧 ThingID 识别出种族、却找不到对应 PawnKind 时仍退回人类。现均明确失败，不悄悄替换物种。已加载种族但原 PawnKind 缺失或不匹配时可选择同种族的其他 PawnKind。

旧载荷缺少新字段仍可读取；优先从 ThingID 的 Def 名称及数字后缀推断种族，重叠名称选择最长匹配。完全没有可用种族信息的旧普通关系保留原人类回退；无法解析的 `Bond` 不回退为人类。因此不能宣称本改动能恢复所有历史缺失信息。

验证：

- 客户端 4 个定向组通过，覆盖 Scribe、共享采集、载荷读取与重复流转身份。种族解析新增 16 种边界 × 4 种文化；采集覆盖 3 种种族 × 礼物／支援发送／返回 × 4 种文化。种族 Def 为隔离测试夹具，不是加载真实 HAR 内容模组。
- 实际礼物和支援载荷读取／转换保留关系种族、PawnKind 和 Unicode 名称，支援兼容两种已有字段大小写及返回转换。
- `Client.Core.Tests` 10 项通过，包括新增字段的客户端／服务端 JSON 往返和缺失字段的旧 JSON。
- Events 51 项单元检查通过，含安全包 schema；服务端物品流转专项通过，覆盖跨文化映射、包存储及重开。
- 测试中另发现先前快照重构使 `Client.Core.Tests` 缺少共享故障类型而编译失败。已在 `12b2b2e2` 将类型移至独立源码并补齐链接引用，未复制类型或引入游戏运行库依赖。

证据目录：`LocalOnly/third-party-review-2026-10-20/`，含修复前失败、修复后单元及待提交内容隔离验证记录。本次没有运行实机 RimTest、NetworkSmoke 全场景或全量回归；没有将其他任务中的 RimTest 扩展、运输船、远程任务清理或世界默认 tile 改动混入此提交。
