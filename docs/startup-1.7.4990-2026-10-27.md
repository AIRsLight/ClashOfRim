# 1.7.4990 更新后，工坊 0.4.9 启动异常复现

## 结论

已复现用户描述的“进入主菜单后报错、缺少多人入口”。游戏主菜单加载完成，但 ClashOfRim 主程序集在类型检查时被拒绝加载，因而没有完成模组初始化和菜单补丁。不是客户端登录、服务端协议或存档问题。

首个主故障：

```text
ReflectionTypeLoadException getting types in assembly AIRsLight.ClashOfRim
Method 'GuiToDevicePoint' in type 'MaskedBackend' ... does not have an implementation.
at Verse.ModAssemblyHandler.AssemblyIsUsable(Assembly asm)
```

`Source/Client/Ui/ClashPasswordField.cs` 中的 `MaskedBackend` 直接实现 `UnityStubs.Gui.IGuiBackend`。当前游戏接口包含 `Vector2 GuiToDevicePoint(Vector2 guiPoint)`，旧包未实现这个成员；冻结的 `1.7.4985 rev1269` 接口没有该成员。加载器调用 `Assembly.GetTypes()` 时即失败，尚未进入密码输入界面。

随后防御点、远程地图、成就和多人按钮等 Def 报“找不到类型”，是整个程序集未能加载的后果，不能据此逐个修复 Def。Melee Animation 的后置补丁也扫描到此无效程序集，并因此报同一个 `MaskedBackend` 类型错误。

## 输入

- 当前安装与实际运行版本均为 **1.7.4990 rev1069**，全部五个 DLC、简体中文、`zh-CN`，原生渲染隐藏窗口，`DOTNET_PROCESSOR_COUNT=4`。
- 使用最后一次工坊上传的 **0.4.9-dev.9ffe8411** 原始产物，不使用工作区开发 DLL。主包 142 个文件和兼容包 20 个文件均通过当时上传清单的哈希校验；本机订阅 DLL 也与该产物一致。目录时间不能用于判断包是否过期。
- 主 DLL SHA-256：`9FF48051AFDCD8EC30A3885BBCC67790FC0EB36F7F5486ADF502FAB58EB67720`；完整列表运行目录中再次核对了实际加载副本。
- 复用此前 `bbb.rml` 验证使用的 `meta.modIds`：**117 项**，除加入 RimTest 外不调整顺序。`runtime.hello` 确认排除 RimTest 后实际加载 117 项，逐项顺序完全一致。第三方内容取当前 Steam 安装版本。
- 文件的 `modList.ids` 另含 YART，共 118 项；本轮遵循此前测试的 117 项基线，未加入这一个额外条目。
- 独立新 profile，默认模组设置。未覆盖用户配置、存档或工坊文件，未连接服务器、未新建殖民地。

## 定向实测

| 配置 | 主菜单就绪 | 原生 ERROR/FATAL | 主程序集缺失成员 | 多人入口 |
| --- | --- | --- | --- | --- |
| 此前完整 117 项列表 + RimTest | 是 | 34 | 复现 | 缺失 |
| Harmony、Core、五 DLC、主模组 + RimTest | 是 | 28 | 复现 | 缺失 |
| Harmony、Core、五 DLC + RimTest | 是 | 0 | 无 | 不适用 |

三组均观察到 `startupReady=true`、`longEventPending=false`。两组包含产品的配置还保留了实际 `ui.snapshot` 主菜单按钮，未出现多人入口。测试证实的范围是启动和主菜单，不是业务全量回归。

另有一次基础配置尝试因调查脚本向 `AdditionalMod` 传入空元素，在启动游戏前失败；修正调查脚本后使用新目录重试。此基础设施失败未计入上表。

完整列表还记录到：

- Kiiro Story 两张 `CuringSnackA.JPG` / `CuringSnackB.JPG` 被新引擎报告文件内容与扩展名不匹配。
- Ratkin Gene Expanded 的 `SYSPatch_ReplaceSYSDrawPrefix` 找不到补丁目标方法。`Meow.Patch` 与 `RatkinGeneExpanded.Patch` 均因此静态初始化失败。

这些额外记录需要在主程序集修复后的完整列表中再次观察，不能用来解释最小配置也出现的多人入口缺失。

## 修复要点及边界

主模块需要适配新的 GUI 后端接口，将 `GuiToDevicePoint` 转发给真实后端，保留设备坐标转换，不能简单返回原坐标。应同时验证编译引用与运行时接口一致，并定向回归密码遮罩、原生编辑/粘贴、IME 以及缩放下候选窗口位置。今天的原生 `GUI.PasswordField` 仍未使用 `maskChar`，直接移除现有遮罩也会恢复明文显示。

本轮仅复现、定位和记录，没有修改产品代码、发布构建或上传工坊。冻结 1.6 不受影响。

## 后续修复（0.4.10）

密码遮罩后端已将新增的 `GuiToDevicePoint` 转发给原生后端，主包和兼容包的共享编译引用升级为 `1.7.4990-unstable`。保持现有密码遮罩和原生编辑行为。

使用实际 1.7.4990 程序集编译后，`world entry forms` 客户端测试组通过（四种文化）。密码框测试覆盖遮罩、输入、删除、选择、粘贴、IME 和异常后后端恢复；新增非恒等缩放及偏移断言，确认 IME 候选位置使用设备坐标。此处记录的是单元验证，最终发布包的启动验证另行记录。

从 `33bb56939` 导出的干净源码（包括固定的子模块提交）再次构建，相关客户端 12 组测试和两个包的内容隔离检查通过，冻结 1.6 的主包 68 个文件、兼容包 8 个文件校验通过。最小产品实机启动在 1.7.4990 上通过：主程序集成功初始化、175 个补丁方法、0 条 ERROR/FATAL，主菜单出现“加入服务器”，触发后打开 `ClashOfRimServerEntryDialog`。隔离实例的原生 Steam API 提示经读取确认后通过“忽略”关闭；没有登录或连接服务器。

修复验证证据：`G:/RimTestRuns/release-0.4.10-2026-10-27/minimal/20261027-130845-5ab07599/`。成功后已按策略回收游戏副本，保留报告、日志和配置。完整 117 项列表将使用最终发布产物验证；不把这一最小配置结果描述为完整列表或业务全量通过。

## CI 产物完整列表验证

使用 `1f3898c2` 的 CI 模组产物运行此前 117 项列表，实际加载数量、顺序逐项一致，主包和兼容包的运行副本哈希与下载产物一致。主菜单就绪且无待处理加载事件，出现“加入服务器”；调用原按钮后成功打开 `ClashOfRimServerEntryDialog`。主程序集成功初始化，旧的缺失接口、缺失产品类型及 Melee Animation 连带错误不再出现。相同主包 DLL 的 16 组客户端专项测试通过。

仍有 **4 条已知第三方 ERROR**：Kiiro Story 两张图片格式错误，以及 `Meow.Patch` / `RatkinGeneExpanded.Patch` 的静态初始化失败（共同指向 `SYSPatch_ReplaceSYSDrawPrefix` 的目标缺失）。因此结论仅为本模组启动修复通过，不能称完整模组列表无报错或游戏业务全量通过。证据：`G:/RimTestRuns/release-0.4.10-2026-10-27/full/20261027-131851-bbda1aae/startup-report.json`。

一次加载完成前的 `game.state` 探测超时，在日志出现 RimTest Ready 后重试成功。最终上传前仍需确认发布工作流通过及包哈希；之后的交易集成测试契约修正仅涉及测试和文档，不改变本次实机验证的产品源码。

## 证据

本机证据根目录：`G:/RimTestRuns/update-startup-2026-10-27/`，汇总为 `startup-summary.json`。

- 完整列表：`full/20261027-124745-59b26a9e`，主错误位于 `stdout.log:359`，另存 `hello.json`、`game-state.json`、`ui-snapshot.json` 和 `startup-report.json`。
- 最小产品：`minimal/20261027-124900-a6d658d9`，主错误位于 `stdout.log:275`。
- 基础对照：`vanilla/20261027-125443-0dd3c3ef`。
- 调查启动脚本：`LocalOnly/Start-UpdateStartupProbe.ps1`；只使用现有共享缓存/运行目录策略。失败运行保留现场，基础对照成功后回收游戏副本，所有测试游戏进程已停止。未清理历史测试目录。
- 当前 UnityStubs SHA-256：`DC3D7FAA66188952C118B23D5ACFD40AC6A4DFB79CBCBE971D0A81C2F60D26CF`；旧 1.7.4985 文件为 `EA962C9274514E0633D4E4C5471A1A98AFF5FC52AF20CA2E506C16DCD8BE6F81`。
- REA 静态身份记录：`ev_0b992ed217def79eb8da864644152010a46eda29fd0d273d1b37b49363a7105c`。REA 全成员读取受 64 MiB 表示预算限制，未据此宣称完整静态覆盖；使用已有 ILSpy 对新旧 `IGuiBackend` 和当前 `GUI.PasswordField` 定向反编译确认接口差异。运行时归因来自上述真实游戏日志与对照。
