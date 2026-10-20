# 重构后遗留代码与推送准备检查

检查起点：`e890d718` 加既有工作区；2026-10-20 获取三个仓库的远端引用。主仓库起点领先 `origin/dev` 460 个提交，远端没有本地缺失的提交。本轮检查、清理和提交，没有执行推送或全量回归。

## 已完成的清理

| 提交 | 变更 | 判定依据 |
| --- | --- | --- |
| `6c50efd9` | 删除 `CoreThingReferenceMetadata.TechprintProjectDefName` | 内部辅助查询没有源码、测试或字符串反射调用；保留仍在使用的科技蓝图识别和研究项目目录 |
| `6c50efd9` | 删除 `ClientWorldConfigurationRuntime.IsValidTile` | 旧的单层整数格子判断没有调用，当前选址已有实际使用的分层逻辑 |
| `6c50efd9` | 删除 `ClientDiplomacyPresentation.FormatDiplomacyKind` | 没有调用；旧字符串分派不再承担事件类型展示 |
| `6c50efd9` | 删除 `AchievementRegistry.ListAggregatesLocked` | 编译器分析及全仓库引用检查均确认未使用；正式查询路径不变 |
| `0e9b2067` | 删除 `ClientSnapshotPublication.PublishCurrentGame` 及其 `IClientSnapshotGame` 字段、构造注入 | 生产已经显式传入所属 `IClientSnapshotGameSession`；这个临时绑定当前游戏的入口只剩测试调用。测试改用正式 `Publish`，保留空快照、lineage、成就保留及持久化顺序断言 |

最后一项同步调整装配点和测试夹具的构造参数，没有删除发布断言或增加另一套发布逻辑。生产发布不再提供隐式重新绑定当前游戏的便捷入口。

## 检查方法与保留边界

对 `Source`、`Tools`、`ThirdPartyCompat/Source` 的 1,955 个 C# 文件建立 Roslyn 语法引用清单，排除 `bin`、`obj`、冻结 1.6 和独立 RimTest 源码；再对候选项检查字符串反射、Def XML、注册描述符和测试契约。partial 声明仍为 **0**。

初始清单有 538 个名字没有生产标识符引用，清理后为 533。**这是候选数量，不是死代码数量，也不是 533 项已逐一完成语义审计。** DTO、公开库 API、引擎入口和测试入口都会进入清单；同名符号也可能掩盖未引用成员，因此同时运行客户端、服务端的语义分析。

客户端和服务端最终 `IDE0051`／`IDE0052` 检查均退出 0。构建未报告 `CS0162`／`CS0168`／`CS0169`／`CS0219`／`CS0414`／`CS8321` 这组不可达或未使用代码诊断。业务模块未检出 `#if false`、`NotImplementedException`、TODO／FIXME／HACK 标记。这些结果不证明所有动态路径均可达。

明确保留：

- Harmony 的 `TargetMethods`、`Transpiler` 及补丁类型；游戏通过属性和反射发现它们。
- 内置兼容包的 `Descriptor`、Def XML 引用类型、编译器兼容 shim；没有静态类型引用不代表没有入口。
- 商店窗口的 `SetItemStuff`／`SetItemQuality`／`SetItemHitPoints` 等桥接方法，`CatalogCommands` 和对应测试通过字符串反射调用。
- 已记录的 Mod 动作、公开网络客户端、第三方兼容 API 和插件转发契约，遵循[模块拆分验收中的保留条件](partial-module-completion-audit-2026-10-19.md)。
- 仍在诊断或测试中使用的内部接口，不按“生产引用为零”批量删除。例如会话失效模拟、旧交易取消路径测试与商店延迟动作测试。本轮只迁移了确认已经多余的快照发布入口。

`IDE0060` 另报客户端 39 个、服务端 2 个未使用参数，含委托／公开兼容签名和内部展示参数，未机械删参。干净客户端测试构建仍有 **82 条警告**（69 条可空性、13 条 `SYSLIB0050`）；没有把这次检查描述为零警告或所有代码质量问题均已关闭。

## CI 和检出依赖

发现既有未提交的 CI 改动错误地把 RimTest 排除在普通回归之外：

`Test-Regression.ps1 → ClashOfRim.Save.Tests → ClashOfRim.GameTests → RimTest.Client / RimTest.Testing`

递归检查得到 12 个项目依赖，涵盖两个子仓库。因此：

- 回归工作流显式检出 `ThirdPartyCompat RimTest`，失败信息提示两个仓库的读取权限。
- 保留并提交已有的发布构建调整：纯客户端／服务端打包只检出 `ThirdPartyCompat`；发布流程调用的回归工作流负责 RimTest。
- 中英文 README 说明正常产品构建与普通回归的依赖差别，纠正“普通回归无需 RimTest”。

仅做本地项目依赖闭包验证，没有访问或验证 GitHub Actions 的秘密配置，也没有触发云端工作流。

## 验证

| 检查 | 结果 |
| --- | --- |
| 第一轮客户端定向单元 | 5/5 组，商店反射入口、世界基线／准备、外交和模块边界 |
| 第一轮服务端定向单元 | 3/3 组，成就查询、排行榜生命周期、模块依赖和 81 个路由 |
| 快照发布清理定向单元 | 7/7 组，上传、重载、事件确认、事务通知和模块边界 |
| 仅含已提交文件的服务端测试构建 | 通过，0 警告／0 错误 |
| 仅含已提交文件的客户端测试构建 | 通过，82 警告／0 错误，使用本地 1.7 运行库 |
| 干净副本定向单元 | 客户端 6/6、服务端 3/3 组通过 |
| 冻结 1.6 | 77 个文件及哈希通过，未修改 |
| 私有成员静态分析、差异空白检查 | 通过 |

干净副本由主仓库 `0e9b2067`、RimTest `1836ba05`、ThirdPartyCompat `54dc6eae` 的 `git archive` 构建，不包含任何工作区未提交文件。它验证提交内容可以独立编译及通过上述单元，不能代替全部业务实机测试。后续 CI／README 改动另经项目闭包和差异检查。

证据：`LocalOnly/push-readiness-2026-10-20/` 中的候选清单、分析器报告、测试日志、干净构建日志及 `regression-project-closure.txt`。干净副本为 `LocalOnly/push-audit-clean/`。没有启动游戏实机或全量回归。

## 实际推送前仍需注意

获取远端引用后确认，主仓库当前记录的两个子仓库提交尚未进入其远端跟踪分支：

| 仓库 | 主仓库引用版本 | 相对远端分支 |
| --- | --- | --- |
| RimTest | `1836ba05a979caee2b005631034b5d006c964ea9` | `origin/main` 领先 4，落后 0 |
| ThirdPartyCompat | `54dc6eae5d196eaa93c6808939b2e6ea600cd1e6` | `origin/dev` 领先 3，落后 0 |

实际推送时先发布两个子仓库的已提交版本，再推主仓库；应使用子模块可达性检查，避免主仓库引用尚未发布的依赖。这里没有执行任何推送。

主仓库仍有本轮开始前的未提交工作，主要是 pawn 关系载荷／传输恢复、远程任务清理、运输船 Def、默认世界 tile 和相关测试；RimTest 也有 DLC／状态选择器等未提交扩展。本轮纳入了审阅过的四个 CI／README 文件，其余既有工作保留。**推送已有提交不会包含这些工作区内容，也不代表它们已经完成此次验收。**
