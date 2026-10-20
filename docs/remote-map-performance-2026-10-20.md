# 远程地图加载性能实测（2026-10-20）

**已复现连续数秒停止更新和重绘。** 本次只测量并补充可复跑场景，没有修改生产加载逻辑。两次独立启动、共六次友方远程地图加载都出现停顿，首次约 3.1–3.2 秒，后续约 2.4–2.7 秒；不能归因于测试命令队列在长任务期间暂停。

## 环境与口径

- 本地 Steam RimWorld `1.7.4980 rev60`，五个官方 DLC，Harmony、ClashOfRim、RimTest 和桥接模组；没有第三方内容模组。
- i7-14700KF、RTX 3060，双客户端和服务端同机，HTTP loopback；进程继承 `DOTNET_PROCESSOR_COUNT=4`。
- 图形模式 `headless=false`，框架使用隐藏窗口 `noWindow=true`；观察端设置为 1280×720、UI scale 1，DevMode 开启以保留原有投影阶段日志。
- 目标是生成后暂停的 250×250 新殖民地地图。细化复测含 34,689 个已生成 Thing、54 个 Pawn（含动物等，非殖民者数量），压缩快照约 1.52 MB，原始存档约 15.39 MB；初测是另一次新建地图，34,894 个 Thing、51 个 Pawn。每场内连续三次加载同一目标，跨场并非完全相同的地图。不是后期大基地压力样本。
- 每轮先完成目标快照上传和地图标记刷新，再开始测量观察动作，避免把准备时间计入加载。首轮指本进程第一次远程投影，不代表操作系统或游戏全部缓存为空。
- 测试专用 Harmony 探针记录 `Root.Update`、`Root.OnGUI` 的真实 Repaint 单调时钟时间、阶段起止时间与线程 ID。开始后先留 1 秒基线，打开后再留 1.5 秒，之后收集并正常关闭远程地图。
- “动作到首帧”从原 `ClientObservationMaps.OpenAsync` 入口到 `ClientObservationRuntime.Open` 返回后的首个 Root Repaint；不是屏幕物理显示延迟或鼠标点击往返延迟。没有进行 OS 输入交互，也不把它表述为 Windows 判定的“程序无响应”。

## 细化复测结果

证据：`G:/RimTestRuns/remote-map-performance-2026-10-20/20261020-060715-b5be1ab9`，原始数据为 `remote-map-performance.json`，包含完整帧时间序列和跨度记录。

| 指标 | 首次 | 第二次 | 第三次 |
| --- | ---: | ---: | ---: |
| 观察动作到加载后首个重绘 | 3,767.50 ms | 3,155.04 ms | 3,186.80 ms |
| 最大 UI 重绘间隔 | **3,136.50 ms** | **2,464.46 ms** | **2,595.78 ms** |
| 最大 Root.Update 间隔 | 3,133.68 ms | 2,459.43 ms | 2,591.09 ms |
| 元数据及快照下载链路 | 616.23 ms | 677.36 ms | 573.87 ms |
| 同步打开 `ClientObservationRuntime.Open` | 2,262.31 ms | 1,936.21 ms | 2,067.80 ms |
| 其中：投影 `TryProject` | 2,242.62 ms | 1,933.96 ms | 2,064.30 ms |
| 其中：Scribe 导入与地图初始化 `TryLoad` | 1,215.84 ms | 1,118.16 ms | 1,107.70 ms |
| 打开返回后的网格重建 `RegenerateEverythingNow` | 616.62 ms | 299.16 ms | 308.34 ms |
| 同步打开期间发生的 Repaint 次数 | **0** | **0** | **0** |

表中“其中”是嵌套跨度，不能将所有行相加。前后空闲段的最大重绘间隔分别只有 **14.13 ms / 13.07 ms**，P95 约 10.3 ms。下载阶段运行在线程池，打开、Scribe 初始化、地图初始化和网格重建均运行在与 Update/Repaint 相同的线程（本次 managed thread 2）。

首次打开进一步拆解：

| 阶段 | 耗时 |
| --- | ---: |
| 校验、解压 | 17.38 ms |
| 原始存档 XML 解析 | 180.67 ms |
| Scribe.InitLoading（读取投影后的临时存档） | 322.28 ms |
| Scribe.FinalizeLoading（引用解析及载入回调） | 106.55 ms |
| Map.FinalizeLoading | 402.44 ms |
| 友方观察地图后处理 | 14.09 ms |
| LongEventHandler 完成回调 | 616.99 ms |

完成回调几乎全部是地图网格重建（616.62 ms）。同步打开与完成回调之后，到首个 Root Repaint 仍约 248 ms；这里只将其记作未进一步拆分的引擎帧尾时间，不能全部算成网格重建或声称已经定位具体渲染函数。

## 原因与改进顺序

1. [`ClientRemoteMapLongEvent`](../Source/Client/RemoteMaps/Runtime/ClientRemoteMapLongEvent.cs) 明确以 `doAsynchronously: false` 执行整个导入回调。实测显示同期 Update 和 Repaint 都停止，加载窗口无法持续刷新。
2. [`RemoteMapSnapshotProjector`](../Source/Client/RemoteMaps/Projection/RemoteMapSnapshotProjector.cs) 把解压、XML 解析、复制、引用改写与清理、Scribe 导入连在同一次同步执行中。下载已异步，但完成下载并未解决后面的主线程停顿。
3. [`RemoteMapRuntimeLoader`](../Source/Client/RemoteMaps/Projection/RemoteMapRuntimeLoader.cs) 将已构造的 XDocument 写成临时 `.rws`，随后 Scribe 再读取解析。首次 `TryLoad - TryLoadFile` 约 64 ms，Scribe.InitLoading 约 322 ms；存在评估减少重复序列化/解析的空间，但不能将这部分全部算成磁盘 IO。
4. 原版地图初始化会安排完成后的网格重建；它在 `Open` 返回后继续阻塞。因此只计 `TryProject` 或 `Open` 会低估用户真正等待的时间。

建议先拆出可独立执行的不可变存档准备阶段，保留游戏对象、全局 ID、Faction、Scribe 及地图注册的明确线程归属；随后处理必须由游戏线程执行的初始化和网格重建的分阶段调度，并持续显示阶段进度。**不能只把整段 `doAsynchronously` 改为 true**：当前过程同时访问全局 Scribe 和现存游戏对象，需要先验证引擎线程契约及会话取消/清理边界。

此次样本中友方后处理只有约 2–14 ms，解压约 17 ms，均不是主要瓶颈；优先微调这两处不会消除数秒停顿。下载统计包含服务端处理和元数据请求，不能据此推断公网带宽。

## 测试与复跑

新增独立 `remote-map-performance` 场景，复用原有联盟和 `RemoteMapScenario.ObserveAlly` 加载/关闭流程，探针仅位于测试桥接程序集。计划固定为 16 项，包括 14 项共有入口/世界/存档检查、联盟及性能采样，不运行完整业务回归。`Test-RimTest.ps1` 对此场景强制保留图形渲染。

```powershell
$env:DOTNET_PROCESSOR_COUNT = '4'
./Tools/Test-RimTest.ps1 `
  -RimWorldPath 'E:/SteamLibrary/steamapps/common/RimWorld' `
  -HarmonyPath 'E:/SteamLibrary/steamapps/workshop/content/294100/2009463077' `
  -RunsDirectory 'G:/RimTestRuns/remote-map-performance' `
  -Profile Tools/RimTestRemoteMapPerformance.json
```

两场各 **16/16 功能与采样检查通过**，包含正常关闭、存档重载、重连和运行错误检查。这是“成功取得有效性能证据”，**不是性能达标**；没有设置因硬件差异而不可靠的固定毫秒通过线，也没有要求必须卡顿才能通过。

- 初测：`20261020-060438-b3564a78`；最大重绘间隔 3198.69 / 2438.93 / 2656.58 ms。
- 细化：`20261020-060715-b5be1ab9`；新增 XML、解压、延后回调及网格重建探针，数值见上表。
- 每场 `inputs.json` 保留产品、桥接、框架、运行器哈希及原有工作区修改；两场探针版本不同，不能声称所有二进制完全一致。
- 定向单元验证通过 3 组：桥接命令注册/权限/独立依赖（79 个命令、21 个模块），场景目录计划/绑定/失败报告，以及原远程地图隔离/战斗重载场景。没有执行完整单元或业务回归。

范围限制：仅友方观察的根地图；未追加袭击专用后处理、进入已有子地图、超大/多地图快照、第三方内容模组和公网延迟矩阵。它们复用部分加载代码，但不等同于本次已测。生产加载逻辑、冻结 1.6 和既有未提交工作均未改动。

## 首次优化：后台准备存档

本节记录后续用户授权的生产优化，上述表格保留为优化前基线。

[`ClientRemoteSnapshotDownload`](../Source/Client/Modules/Maps/ClientRemoteSnapshotDownload.cs) 在下载成功后，将校验、解压、XML 解析交给后台任务。新的 [`ClientRemoteSnapshotDocument`](../Source/Client/Modules/Maps/ClientRemoteSnapshotDocument.cs) 只持有序列化数据，保留原来的 DTD 禁止、空白节点和换行解析规则，不访问游戏状态、Scribe、翻译或兼容钩子。解析结果沿原观察/袭击管线交给游戏线程，一次性取出，释放准备对象对 XML 树的引用。会话/游戏所有权复验和同步准入仍由原流程负责，取消在准备阶段边界检查。

载荷大小、哈希和解压验证移入已有 `ClientSnapshotPayloadCodec`，整档恢复和远程地图共用同一实现，删除投影器内的重复算法。旧的公共 `TryProject` 入口仍可在没有准备结果时同步调用同一解析逻辑。引用改写、阵营和 ID 分配、兼容钩子、Scribe、地图初始化与网格重建继续使用原线程和原生命周期。

曾评估直接从 XDocument 建立 Scribe 的 XmlDocument。本地 1.7 `ScribeLoader.InitLoading` 的静态 CIL 只显示文件入口；项目已有的整档内存适配器可以供以后统一，但直接使用 `XDocument.CreateReader()` 会将缩进文本保留为 Text 节点，与原文件读取结果不同，换行规范化也有差别。本轮没有采用该替换，没有改写原版 Scribe 初始化或临时文件流程。

### 性能复测

运行：`G:/RimTestRuns/remote-map-optimization-2026-10-20/20261020-062510-a4d9d7cf`，图形模式，**16/16**。仍为 250×250、全 DLC、相同显示设置，目标地图 34,517 个 Thing / 53 个 Pawn，存档约 15.23 MB。属于重新建档，与基线规模接近但不是逐字节相同的快照；不能把全部波动归因于代码修改。

| 指标 | 首次 | 第二次 | 第三次 |
| --- | ---: | ---: | ---: |
| 优化前最大重绘间隔 | 3,136.50 ms | 2,464.46 ms | 2,595.78 ms |
| 优化后最大重绘间隔 | **2,821.80 ms** | **2,160.68 ms** | **2,562.28 ms** |
| 优化后动作到加载后首帧 | 3,733.09 ms | 2,961.39 ms | 3,448.84 ms |
| 后台存档准备 | 242.73 ms | 177.01 ms | 250.40 ms |
| 同步打开 | 1,978.82 ms | 1,649.58 ms | 2,002.50 ms |
| 后续网格重建 | 601.48 ms | 288.62 ms | 292.78 ms |

探针确认三次准备分别在线程 36 / 25 / 26，游戏线程均为 2；准备只执行一次，结束后才开始地图打开。新场景将“未在游戏线程解析、没有重复解析、准备与打开不重叠”作为验收条件。`ClientRemoteSnapshotDownload.DownloadAsync` 现在包含后台准备时间，和旧版本比较下载耗时必须扣除 `ClientRemoteSnapshotDocument.Read` 的跨度。

**本轮减少了一小段连续阻塞，没有解决全部卡顿，也没有证明总加载速度提升。** 三次平均动作到首帧约 3.38 秒，基线约 3.37 秒；剩余导入与网格重建仍连续占用主线程。进一步拆分需要单独处理引擎线程约束、分阶段游戏状态以及失败清理，暂不扩大改动。

定向单元 **10/10 组通过**，覆盖 raw/gzip、大小/哈希/编码错误、损坏 gzip、非法 XML/DTD、Unicode/空白/换行语义、准备结果只消费一次、取消及原有观察/袭击准备和提交/整档恢复/依赖边界。

共用管线另运行子地图专项 `G:/RimTestRuns/remote-map-optimization-pockets-2026-10-20/20261020-062658-48f4d3cd`，**18/18 通过**：观察子地图清理、袭击进入子地图、战斗保存重载、返回、结算，以及存档重载与重连。本场采用 Headless 验证生命周期，不用于 UI 性能结论。没有运行全量回归。

实机与定向单元完整记录位于 `LocalOnly/remote-map-optimization-2026-10-20`；各实机目录的 `inputs.json` 保存构建和原有工作区状态。冻结 1.6 和本轮开始前的未提交修改保留。
