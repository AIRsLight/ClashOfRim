# 多语言 UI 实测（2026-10-20）

## 方法和范围

RimWorld 1.7.4980 rev60，原生图形模式。RimTest 在真实窗口 Repaint 中记录 `Widgets.Label` 的矩形和当前字体的 `CalcSize/CalcHeight`，保存原生渲染 PNG。按钮最终绘制的文字也纳入测量；自动判定文字宽高超出区域，并人工查看重点截图。此结论不是由翻译字符数、无图形模式或单元测试推断。

专项包含多人窗口 9 个页面、管理面板 9 个页面，以及交易、商店、支援、搬家、账户注册、会话恢复、选择器和兼容性窗口，共 45 种窗口/页面状态。商店使用真实管理接口建立并断言 6 种商品，覆盖七位价格和大库存；商店编辑另含衣物材质、品质、耐久和收购条件，以及滚动到底部的状态。长名称夹具混合中文与西里尔文。所有确认窗口仅打开，不提交破坏性操作。

显示请求为 1280×720、1920×1080、1920×1080 / 125%。游戏会修正窗口尺寸与 UI 比例，必须以每份 `ui-layout.json` 的 `pixelWidth/pixelHeight/scale/screenWidth/screenHeight` 为准。例如 720p 实测自动缩放为 93.75%，逻辑区域为 1365×768，不能宣称为 720p / 125% 通过。

## 已复现和修正

1. 交易创建的俄语“添加”按钮宽度只有 54，实际文字宽度 61；改为由当前字体测量按钮宽度，并让左侧物品名同步避让。
2. 交易信标范围说明、请求条件标题只有单行高度；改为按实际换行高度分配空间，下方搜索/列表随之移动。
3. 雇佣职业和时长标签的俄语文本超过 72/180 宽度；标签测量后定位相邻选择器/滑条。
4. 成就页面俄语标题宽 120，原区域宽 110；标题宽度与玩家选择器位置联动。
5. 管理清单包 ID、商店长物品名未经省略直接绘制；复用统一的省略及完整文字 Tooltip，商店物品名不再与选择按钮重叠。
6. 支援目标双长名称、俄语模式说明被单行区域截断；按文本高度布局并增加窗口空间。
7. 注册窗口的长账户名被截断；使用统一省略标签并提供完整 Tooltip。
8. 大库存下的俄/韩商店卡片库存说明换行截断；库存独占整行，价格、总额和库存均保留完整 Tooltip。
9. 商店编辑的俄语字段标签截断，完整收购条件超出右侧面板；统一测量字段列宽，编辑区域按内容高度滚动，固定底部保存/取消按钮。
10. 俄/韩特性描述含显式换行，旧省略算法仍会生成多行；统一单行标签先消除换行，Tooltip 保留原文。

文字省略实现从交易工具移至通用 UI 工具，旧交易入口转发到同一实现。没有加入全局缩小字体、修改翻译以迎合固定尺寸或忽略错误的逻辑。

## 执行记录

- 首轮中俄：`G:/RimTestRuns/ui-layout-2026-10-20/20261020-035916-68234da9`，198 个样本，确认上述缺陷。前两次探针/语言名配置失败保留在本地日志中，不计通过。
- 修复后英日（基础游戏）：`G:/RimTestRuns/ui-layout-2026-10-20/20261020-040426-06dcd38a`，198 个样本无文字溢出，客户端运行错误检查通过。
- 扩展俄韩全 DLC：`G:/RimTestRuns/ui-layout-expanded-2026-10-20/20261020-040740-9becba9f`，建图阶段报错，入服错误检查失败，未执行 UI 检查。俄语规则解析报 `Bad string pass when reading rule [tradeAdj_fem] [tradeNoun_fem]`，堆栈位于原版 `RulePack/NameGenerator/FactionGenerator`。游戏随后继续执行 `Game.InitNewGame`，不能将此记录描述为进程崩溃或世界生成未完成。未屏蔽该错误；俄韩后续限定 Biotech，其他语言继续全 DLC。
- 扩展俄韩 + Biotech：`G:/RimTestRuns/ui-layout-expanded-2026-10-20/20261020-040903-2f729c01`，264 个样本，复现第 8–10 项；不能计为通过。

最终扩展矩阵目录：`G:/RimTestRuns/ui-layout-final-2026-10-20/20261020-041317-196e5400`。

- `ui-russian-korean/20261020-041323-37550bc2`：俄/韩 + Biotech，270 个样本无文字溢出，15/15 用例通过。
- `ui-english-japanese/20261020-041737-dac5b1a1`：英/日 + 全 DLC，270 个样本无文字溢出；UI、存读档、重连、运行错误检查通过，但整组 **14/15**，文化同步检查失败。
- `ui-chinese-english/20261020-042204-3e2b71fd`：中/英 + 全 DLC，270 个样本无文字溢出，15/15 用例通过。

最终 **3/3 UI 用例通过，810/810 PNG 存在，0 个测量溢出、0 个直接显示的 `ClashOfRim.*` 翻译键**。英文在两个组合中重复作为对照；五种语言各覆盖 45 个状态和 3 组显示设置。整个运行器结果仍为失败（44/45 用例通过、0 跳过），原因是上列文化同步失败，未改写其结果。机器可读汇总及证据文件 SHA-256 见 [验收 JSON](ui-layout-results-2026-10-20.json)。

最终构建的客户端单元测试退出码为 0（日志 266 条 PASS、0 条 FAIL），领域命令契约 77 项通过，1.6 冻结文件校验通过。

## 专项之外发现的问题

英日全 DLC 的 `culture-references` 在打开本轮 UI 夹具之前失败，不能将该组合或整个矩阵标为全绿。`TribeCivil` 的文化均为 `Corunan`、基础均为 `Deity`、意识形态 ID 均为 2，但模因不同：

- 英文服主：`Loyalist, Structure_Animist, TreeConnection`。
- 日文加入方：`FemaleSupremacy, Guilty, HumanPrimacy, Structure_Archist`。

戒律、图标等也不同，因此不是单纯显示名称本地化。加入方 7 次世界观察均保留该组模因。完整原始值在该运行的 `trace.jsonl` / `report.json`。本次 UI 布局修复没有修改世界同步逻辑或放宽文化断言。两项错误的后续定位及修复验证见下文。

## 后续根因核查（2026-10-20）

根因核查阶段仅检查上列两项失败的日志、源码与本机游戏数据，未修改游戏行为、安装目录或测试断言，未重新执行全量回归；当时两项问题均未修复。随后修复的记录见“修复验证”。

### NPC 信仰：不同服务端对象绑定到同一个本地对象

英日运行的 `Clients/20261020-041739-ad82ece1/stdout.log` 给出了直接证据：

- 第 855 行：`owner:server/ideo:2` 经 `TribeCivil` 绑定，名称为 `Loyaltism`。
- 第 892 行：`owner:server/ideo:3` 经 `TribeRoughNeanderthal` 绑定，名称为 `Archotechnic Way`。
- 第 1169 行：本地 `Ideo_2` 同时具有上述两个全局键，同时是这两个 NPC 派系的主信仰，最终名称为 `Archotechnic Way`。同样状态在后续同步中反复出现。
- 对照英文服主 `Clients/20261020-041737-100bf939/stdout.log` 第 1535–1536 行，这两个派系分别引用独立的 `Ideo_2` 和 `Ideo_3`。

调用链位于 `RemoteIdeoMaterializer.TryGetOrCreate`：先由 `TryFindNativeServerFactionIdeo` 按派系 Def 找到本地主信仰，再对其执行 `canonical.CopyTo(ideo)`，最后登记全局键。代码保留了对象引用，却没有确认该对象是否也被其他、应当使用不同服务端信仰的 NPC 派系引用。因此第二次 CopyTo 会直接覆盖第一次写入的模因、戒律等；重复请求或延迟重试无法解决这个冲突。

本机 1.7 的 `FactionIdeosTracker.ChooseOrGenerateIdeo` 原本就允许 NPC 派系复用已有信仰：普通分支有 `Rand.Chance(0.2f)` 的复用机会，数量/隐藏派系条件也会进入复用选择。这不是每个派系必有独立信仰的模型。加入端仍调用原版 `WorldGenerator.GenerateWorld` 生成派系，再应用服务端配置。实测已证明两端引用关系不同；语言导致命名随机消耗不同是合理的触发解释，但本次没有逐个追踪随机调用，不能据此断言日语或某个特定模因本身有问题。

协议也有对应缺口：`ReadCurrentWorldIdeos` 通过 `FindFactionForIdeo(...).FirstOrDefault(...)` 只导出一个 `FactionDefName`。它不能完整表达“一个信仰被多个派系共享”，接收端的 `FirstOrDefault` 也无法区分同一种 Def 的多个派系。仅遇冲突就克隆一个对象，仍无法完整恢复服务端的共享关系。

修复方向：明确同步派系实例与主/次信仰全局键之间的关系，再通过现有包载入器和目录逻辑恢复引用；只在不会串改其他派系时原地更新。已有 pawn 的信仰引用也须按其所属关系处理。定向用例至少包含服务端独立/客户端共享、服务端共享/客户端独立、同 Def 多派系、重复应用、读档/重连，以及英日全 DLC 的原失败组合。不要用统一语言或反复重试掩盖引用关系缺失。

### 俄语命名规则：Odyssey 自带翻译缺少规则前缀

已直接读取本机 `E:/SteamLibrary/steamapps/common/RimWorld/Data/Odyssey/Languages/Russian (Русский).tar`。包内 `DefInjected/RulePackDef/RulePacks_Namers_Factions.xml` 第 136–138 行为：

```xml
<NamerFactionTradersGuild.rulePack.rulesStrings>
  <li>r_name->[tradeAdj_mas] [tradeNoun_mas]</li>
  <li>[tradeAdj_fem] [tradeNoun_fem]</li>
```

第二条缺少 `r_name->`；同一规则的正确结构应为 `r_name->[tradeAdj_fem] [tradeNoun_fem]`。原版 `TradersGuild` 的 `factionNameMaker` 正是 `NamerFactionTradersGuild`。该坏字符串在 Core 与五个 DLC 的俄语包扫描中只命中 Odyssey。

错误链与日志完全一致：

1. `Rule_String(string)` 要求 `keyword->output`，匹配失败后记录 `Bad string pass...` 并返回，留下空 keyword。
2. `GrammarResolver.AddRule` 使用空键，产生 `ArgumentNullException`，记录 `Failed to resolve text. Trying again with English`。
3. 日志随后进入选址页和 `Game.InitNewGame`。`shared-world-entry` 的失败来自 `ScenarioRuntimeDiagnostics.CheckEntry` 检测到首个 error；不存在该记录证明的进程崩溃。

我们的 `InvariantGameDataCulturePatch` 仅暂时切换数值解析文化，不改写 `rawString`；本机压缩包本身已有上述坏条目。因而这是已安装 Odyssey 俄语数据的语法缺陷，和前述 NPC 信仰绑定错误独立。未另行启动纯原版对照进程；结论依据原始包、解析器和实际调用堆栈。

如在模组中兼容，应仅修复这个已知规则中的精确坏字符串，保留其他翻译和原版英文回退，不全局吞掉语法错误，也不直接改 Steam 安装文件。可用俄语 + Odyssey 的最小组合和既有俄韩全 DLC 组合定向验证，再恢复 UI 矩阵的对应覆盖。

证据身份：俄语 tar SHA-256 为 `d5d65a67ecb59052aedd59f6f6d97da8dc686fc58b330290b9113c49acf0b717`；上述成员 SHA-256 为 `b4758cf9f6881816c8ca10002585bd3e1bed38c2b7cc2730cb8835655f47526e`。当前 `Assembly-CSharp.dll` SHA-256 为 `eff7d97949f919fb2b151ebfb906e9be749b63410b7dbda78eeebb7972b245c3`；REA 元数据检查达到容量限制，方法行为另外使用本地 CIL 探针核对。提取的 XML、哈希和两段方法 IL 保存在 `LocalOnly/ui-error-diagnosis-2026-10-20/`，不加入发行包。

## 修复验证

### 派系信仰关系

- 信仰目录增加 `factionBindings`，记录派系 Def、同 Def 实例顺序（持久化 loadID 排序）、主/次信仰槽；服务端规范化和持久化保留该字段，玩家自有信仰不能注入 NPC 绑定。
- `ServerFactionIdeologyBindings` 统一采集及恢复关系。整个目录先选择或载入独立的规范对象，再更新派系和对应 NPC 的引用，复用既有 `.ideo` 载入器。保护玩家对象及非所属派系，引用修复不调用改宗流程，不改变信念度或信仰历史。单条信仰查询不清空其他条目的次信仰槽。
- 旧目录的唯一 Def 继续兼容；同 Def 多派系缺少实例信息时明确拒绝，不猜测第一个派系。旧目录未记录的共享关系无法凭空恢复；完整关系保证要求客户端、服务端均更新，并使用包含该字段的世界基线。
- 定向客户端测试通过：共享拆分、独立合并、顺序反转、同 Def 多派系、次信仰、NPC 与玩家引用隔离、重复应用、清空目录缓存后重新绑定、旧数据歧义拒绝。服务端摘要/规范化/持久化用例通过。
- 英日全 DLC 原配置去掉 UI 采样，仅执行世界及会话专项：`G:/RimTestRuns/ui-error-fixes-2026-10-20/20261020-054620-cffe56b9`，**14/14 通过**，含文化、存读档、重连及运行错误检查。RimTest 现在同时比较次信仰和两端共享对象的关系；不会以相同文本掩盖对象绑定错误。之后对单条目录查询保留其他次信仰槽的补充由定向单测验证。

### Odyssey 俄语规则

- Odyssey 兼容包在原版首次解析 `NamerFactionTradersGuild` 前，仅给精确坏字符串 `[tradeAdj_fem] [tradeNoun_fem]` 补上 `r_name->`。兼容规则列表的两个读取入口，沿用原版解析及缓存；不覆盖整份翻译，不修改安装文件，不拦截或忽略其他错误。
- 单测直接执行原版规则解析，验证两个读取入口、重复读取、已经修正的翻译、英文回退未改动，以及其他规则包或其他坏字符串继续报错。
- 俄韩全 DLC 世界及会话专项：`G:/RimTestRuns/ui-error-fixes-2026-10-20/20261020-054958-fa2d2c26`，**14/14 通过**，包含原来失败的入服、文化同步、存读档、重连及运行错误检查。
- 恢复 `Tools/RimTestUiLayouts.json` 中俄韩组合的全 DLC 配置，供后续 UI 专项使用。这次未重跑 810 张 UI 截图，不将上述世界专项当作新增视觉验证。
- 两项合并后的相关客户端单测 5 组通过，额外目录/兼容性/模块边界 5 组通过；服务端意识形态摘要测试通过。1.6 冻结校验 77 文件通过。未执行全量功能回归。

## 复跑

```powershell
$env:DOTNET_PROCESSOR_COUNT = '4'
./Tools/Test-RimTestMatrix.ps1 `
  -RimWorldPath 'E:/SteamLibrary/steamapps/common/RimWorld' `
  -HarmonyPath 'E:/SteamLibrary/steamapps/workshop/content/294100/2009463077' `
  -CatalogPath Tools/RimTestUiLayouts.json `
  -RunsDirectory G:/RimTestRuns/ui-layout
```

可用 `-Scenario ui-russian-korean` 等仅复跑一个语言组合。专项强制保留图形渲染，不接受 Headless 作为视觉结果。沿用矩阵运行器，不复制另一套启动/报告逻辑。

## 结论边界

自动测量覆盖文字容器，截图用于确认截断及相邻布局；它不是所有控件重叠、鼠标命中、Tooltip 出现位置的形式化证明。当前不覆盖任意第三方字体/翻译、系统 DPI 组合、所有玩家业务状态、聊天长历史、礼物满库存以及所有成就/欠款详情的组合。原生选择器只对实际加载的内容负责。未执行全量功能回归，1.6 冻结内容未修改。
