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
