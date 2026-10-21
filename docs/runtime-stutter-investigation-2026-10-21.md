# 运行期间歇卡顿：bbb.rml 定向性能排查

## 结论

**已确认 ClashOfRim 0.4.2 存在会造成间歇卡顿的客户端主线程路径。** 在用户此前提供的模组列表、固定自定义文化、Miho 单人剧本下，殖民地定期登记触发约 250–400 ms 的停顿，随后同步结果应用又产生约 120–340 ms 的停顿。加速时触发间隔约十几到几十秒，与本次描述一致。

移除 ClashOfRim 及其第三方兼容包后，同一初始存档及其余模组的对照不再出现这组周期尖峰。原版自动存档自身仍有一次约 317 ms 的停顿，因而不能把所有卡顿都归于多人模组。

本轮完成诊断和证据记录，**没有修改产品行为，也没有完成性能修复**。

## 测试输入和方法

- 客户端使用发布附件 `0.4.2-dev.15645279`；实际加载的主 DLL 与发布包以及后续签名探针使用的 DLL 哈希一致。工作区已有未提交产品改动未带入游戏。
- `bbb.rml` 的 117 项模组及顺序；冻结本机创意工坊资源用于两组对照，全部五个 DLC、简体中文、`zh-CN`。使用 `Miho_Start`、固定自定义文化 `Structure_Animist + Individualist`、一个 Miho 殖民者。世界有 23 个文化，250×250 地图，采样开始时约 57 个已生成 pawn。
- 联网组通过既有 RimTest 入服夹具新建殖民地，解除征召，保存 `RuntimePerformanceBaseline.rws`，再采样。无多人模组组在新隔离进程中加载该文件，移除主模组和兼容包，核对其余模组及顺序一致。
- 使用既有隔离的 0.4.1 本地测试服务端宿主和 loopback HTTP；客户端是 0.4.2。本轮定位客户端主线程工作，不评测线上网络或服务端吞吐。
- 本机实际本体为 **1.7.4980 rev60**，列表记载的是 1.7.4982。不是用户当前存档、个人模组设置或另一版本本体的完全复刻。
- 顺序运行单个游戏进程，`DOTNET_PROCESSOR_COUNT=4`，1280×720，`NoWindow` 图形模式而非 `Headless`。记录真实 `Root.Update` 回调间隔、游戏 tick、被测方法同步段的墙钟耗时、分配量及 .NET GC 暂停；不以 RPC 延迟充当帧耗时，也不声称测得显示器呈现延迟。
- 每段变速后预热 10 秒；采样期间不反复 RPC 轮询游戏。探针只观察，不关闭同步、不跳过被测方法。异步方法统计仅代表同步调用段，主线程回调队列另行测量。
- 当前机器约 64 GiB 物理内存，联网采样时物理空余约 17 GiB，但提交额度余量约 3 GiB；已保存两组主机内存状态。结果是该环境实测，不是通用性能阈值。

## 实测结果

下表的“停顿”指相邻 `Root.Update` 间隔超过 100 ms；正常间隔中位数两组均约 31.25 ms。方法统计存在父子嵌套，不能相加当作总帧时间。

| 条件 | 采样时间 | 超过 100 ms 次数 | 最大间隔 | 进程分配量 |
| --- | ---: | ---: | ---: | ---: |
| 联网，普通速度，第 1 段 | 180 秒 | 3 | 271.41 ms | 1,057 MiB |
| 联网，Superfast | 120 秒 | 21 | 723.43 ms | 5,940 MiB |
| 联网，暂停 | 60 秒 | 0 | 57.86 ms | 86 MiB |
| 联网，普通速度，第 2 段 | 120 秒 | 3 | 260.08 ms | 928 MiB |
| 无多人模组，普通速度 | 180 秒 | 0 | 65.19 ms | 299 MiB |
| 无多人模组，Superfast | 120 秒 | 1 | 317.11 ms | 525 MiB |
| 无多人模组，暂停 | 60 秒 | 0 | 31.84 ms | 72 MiB |

两组从同一存档出发，但不是锁步模拟；后续 pawn 行为、事物数量和自动存档计时可有差异，不能把分配量相减解释为精确的模组净开销。

### 周期登记与应用

- 普通速度第 1 段，tick `10001→10002` 帧间隔 **271.41 ms**，`ClientColonyRegistration.PollSitesAsync` 同步段 **268.64 ms**；该间隔 GC 暂停只有 **4.88 ms**。
- 随后 tick `10021→10022`、`10032→10033` 帧间隔分别 **125.41 ms、181.12 ms**，`RunMainThreadActions` 分别 **122.92 ms、178.99 ms**。对应 GC 暂停约 6.20 ms、11.91 ms，主要停顿不能用 GC 单独解释。
- Superfast 在 20,000、30,000、40,000、50,000 tick 附近再次命中该登记调用，同步段分别约 **301.71、308.28、307.86、254.38 ms**。采样内相邻触发约 14.5–28.4 秒。
- 每次普通周期登记约分配 **89 MiB**；响应回调所在帧还出现约 136–162 MiB 的进程分配。采样说明路径和分配集中位置，尚未对每个扩展收集器单独计时。
- 250 tick 的世界对象轮询在首段最大约 2.84 ms；此次主要尖峰不在这个较频繁的轮询中。

### 自动存档

- 联网组在 60,000 tick 的一次 `TrySaveCurrentGameToBytes` 耗时 **716.55 ms**，分配约 **383.5 MiB**，帧间隔 **723.43 ms**。该帧 GC 暂停约 281 ms，是这次保存停顿的一部分。
- 无多人模组组的唯一一次大于 100 ms 停顿落在原版 `GameDataSaveLoader.SaveGame`：方法耗时 **307.17 ms**，分配约 **65.92 MiB**，帧间隔 **317.11 ms**。
- 两次保存并非同一个推进时刻，不能把差值全部归于某个单独保存子步骤；可以确认多人快照捕获是另一处需优化的主线程重操作。

## 已复现的代码问题

### P1：轻量位置轮询连带完整世界扩展收集

[`ClientColonyRegistration.PollSitesAsync`](../Source/Client/Modules/World/ClientColonyRegistration.cs#L74) 每 10,000 tick 启动 Sites 登记，但 [`ExecuteAsync`](../Source/Client/Modules/World/ClientColonyRegistration.cs#L160) 无论 Sites 还是 Extensions 都先收集、复制、序列化全部世界扩展，再判断是否与已接受签名相同。没有变化时也已经支付捕获成本。

内置文化收集遍历世界全部文化，在 [`ReadCurrentWorldIdeos`](../Source/Client/DlcCompatibility/Ideology/Ideos/IdeologyPawnReferenceCompatibility.cs#L303) 中逐个导出原生文化包；[`TryExportIdeoPackageXml`](../Source/Client/DlcCompatibility/Ideology/Ideos/IdeologyPawnReferenceCompatibility.cs#L446) 使用临时文件 `SaveIdeo`、读回、删除。

本轮持久化数据有 23 个文化，文化 payload 为 **2,372,393 字符**，其中文化 XML 合计 **1,249,035 字符**；污染扩展为 903 地块、29,007 字符。这里可以确定文化是最大载荷，但尚不能把整个 268 ms 都记到文件 I/O 一项上。

### P1：观察时间参与内容签名，静止内容也被当作变化

文化 DTO 的 [`UpdatedAtGameTicks`](../Source/Client/DlcCompatibility/Ideology/Ideos/IdeologyPawnReferenceCompatibility.cs#L355) 每次收集都变化，随后作为 `PayloadJson` 的一部分进入完整内容签名。只要 tick 推进，原有 `accepted` 签名比较就无法抑制这类重新登记。

使用发布版 DLL 和这轮真实扩展载荷做独立复现：仅将每个文化条目的 `updatedAtGameTicks` 加 10,000，调用真实 `ClientColonyRegistration.Signature`，签名改变；恢复这些字段后，签名恢复。其他文化内容和其他扩展均保持不变。该例已证明时钟字段足以造成假变化；不代表所有实际文化变化都来自这一字段。

本轮完整签名字符串为 **3,872,725 字符**。位置轮询同时导出大载荷、再把整个载荷序列化成比较字符串，会放大 CPU 和临时分配成本。

### P2：主线程发布链重复进行 JSON 深拷贝

[`ClientWorldPublication.Publish`](../Source/Client/Modules/World/ClientWorldPublication.cs#L48) 先复制 world，随后调用配置投影；[`ClientWorldConfiguration.Project`](../Source/Client/Modules/World/ClientWorldConfiguration.cs#L43) 再复制一次。`ClientProtocolJson.Copy` 使用 JSON 序列化和反序列化实现，两层都在主线程应用链上。

在控制台使用发布版 DLL，对仅包含本轮扩展载荷的 world DTO 连续执行两次 Copy，5 次测量约 **109.56–174.35 ms**，每轮约 **112,000,000 字节（106.8 MiB）**分配。这是实际载荷的组件实验，不是游戏内每个回调的完整耗时归因；其量级与游戏内回调停顿一致。

登记响应与世界版本通知都有配置应用路径；本次已观测一次登记之后出现多次主线程回调尖峰。精确区分每次回调的来源以及扩展应用成本，需要后续修复时补更细的阶段探针。

### P2：多人自动快照仍在主线程执行较重的捕获与清理

[`TrySaveCurrentGameToBytes`](../Source/Client/Core/ClashOfRimGameComponent.cs#L257) 的真实调用已命中最大停顿。原生保存之外还有观察状态清理、谱系标记等处理。当前证据确定入口耗时，尚未独立拆分各个清理步骤。

## 建议修复顺序

1. 在统一世界扩展变更检测中区分“内容”与“观察时间”，先消除 tick 字段导致的无效上传和回传；保留真正的文化、DLC、难度变化同步。
2. 将周期位置检查与完整扩展捕获解耦；文化包按真实变更重新生成，保留重载、修改文化以及第三方改动时的失效机制，避免引入永久陈旧缓存。
3. 明确已接管 DTO 的所有权，在同一发布链中复用一次隔离副本；按服务器版本或内容避免重复应用同一世界扩展。不能直接删除会话/世界归属校验。
4. 拆分自动快照计时；原生 Scribe 留在主线程，将已经捕获后的纯字节/XML/压缩工作按现有快照流程移出主线程，并保持快照确认和谱系一致性。
5. 修复后复用此次存档、模组资源和采样条件做定向对照，再把稳定的场景及断言接入正式 RimTest 性能设施。无需为这一步重跑全量游戏回归。

## 证据与复跑

- 根目录：`G:/RimTestRuns/bbb-runtime-performance-2026-10-21/`。
- 联网：`20261021-144618-1-release-miho-fixed/`；对照：`20261021-145826-without-product/`。
- 各目录的 `runtime-performance.json` 保留方法统计、长调用、帧间隔、GC、tick 和地图规模；`trace.jsonl`、`report.json`、`Clients/group.json` 保留运行及退出信息。
- `verification-summary.json` 保留发布 DLL 哈希与报告；`ProbeSources/` 保留本地探针、运行脚本、分析脚本、签名实验及结果。签名实验使用的是发布 DLL，不是带本地未提交改动的编译产物。
- 复跑入口：`LocalOnly/RuntimePerformance/Run-Online.ps1 -Attempt 2 -MihoFixedIdeology`，随后 `Run-Control.ps1 -OnlineRoot <该轮目录>`。这些是当前机器的本地调查夹具，依赖已准备的发布包、模组目录和隔离测试宿主，尚不是可移植的 CI 场景。
- 启动期有已知第三方初始化错误；本次运行采样期间未发现持续异常刷屏，未把启动错误计为周期卡顿原因。
- 两个测试游戏进程和隔离服务端已退出；未连接、修改线上世界。本轮没有运行完整业务回归。
