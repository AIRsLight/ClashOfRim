# RimWorld 1.7 启动 CLR 崩溃诊断

日期：2026-10-17。关联 [账户生命周期测试报告](rimtest-account-lifecycle-2026-10-17.md)。本次只诊断启动阶段；不将进程存活或 `runtime.hello` 当作游戏业务验收。

## 结论与置信边界

**崩溃不依赖 ClashOfRim 的业务代码。** 已在仅启用 Harmony、RimTest、原版和五个 DLC 的独立配置中复现相同的致命栈；崩溃转储的已加载模块也没有 ClashOfRim 或业务测试桥。

**最有力的原因是 Harmony 内置旧 MonoMod 的 JIT 重入缺陷，与 Waylith CLR 的原生调用存根编译相互作用。** 四份转储具有同一个故障位置、同一个无效地址和同一个 token。二进制中的旧实现，以及 MonoMod 已合并的修复说明，与这个故障机制吻合。尚未用包含该修复的 Harmony 构建做 A/B 验证，不能宣称修复已验收；也未完整追踪本机哪一次嵌套编译释放了状态。

`DOTNET_PROCESSOR_COUNT=4` 只能作为当前测试环境的缓解措施。它降低并发、改变启动时序；不会修复已经存在的编译状态生命周期缺陷。

## 实际输入

- 游戏：Steam `1.7.4980 rev60`，Windows x64，Headless，主机可见 28 个逻辑处理器。
- 游戏自带运行时：`Microsoft.NETCore.App 10.0.10-waylithclr.dev`，具有 `WaylithClr.*` 配置；不是机器上安装的标准 .NET 运行时。
- Workshop Harmony：`3.0.0.0-rc.3+3c0e32cd3fd20e4ae27c47cb8c888844766f9f1a`。失败运行、成功运行及当前 Workshop 的 `0Harmony.dll` SHA-256 相同。
- 源码编译引用 `Lib.Harmony 3.0.0-rc.2`，但产品、RimTest 和业务桥的包没有各自携带 Harmony/MonoMod DLL。实际运行使用上述 Workshop 包。
- 原失败进程和本次新失败进程的转储均只发现一份 `0Harmony.dll`；旧 1.6 路径没有参与加载。
- 精确二进制、运行配置、日志及转储哈希见 [机器证据索引](startup-clr-crash-evidence-2026-10-17.json)。原始转储、日志和反编译结果只保存在本地。

## 直接证据

### 1. 无业务模组也能复现

运行：`G:/RimTestRuns/startup-diagnosis/20261016-165526-ba5c0d/rimtest-1-A`。

启用包仅为 `brrainz.harmony`、`ludeon.rimworld`、Royalty、Ideology、Biotech、Anomaly、Odyssey、`airslight.rimtest`。中文/zh-CN，未设置处理器数限制，无游戏用例执行。PID 100040 以 `0xc0000005` 退出。

虽然测试复用此前隔离运行的只读 Game 二进制目录，实际启用列表来自新的独立 Profile；因此不能靠可执行文件路径中的 `account-all-dlc-final` 判断本次加载了业务模组。新转储模块列表进一步验证了这一点。

致命调用链由外至内为：

`WaylithJobRuntime.WorkerLoop → ConcurrentBag.TryTake → ThreadLocal.CreateLinkedSlot → Lock.TryEnterSlow → Thread.SpinWaitInternal → MonoMod.CompileMethodHook → InvokeCompileMethod`。

### 2. 四份转储对应同一处非法读取

| PID | 场景 | 故障模块偏移 | 读取地址 | 保存上下文中的 RCX / token |
| --- | --- | --- | --- | --- |
| 70836 | 原全 DLC 账户测试 | `coreclr.dll+0x1dbae0` | `0x59` | `0x51` / `0x06000003` |
| 93396 | 原全 DLC 重试 | 同上 | 同上 | 同上 |
| 91696 | 原全 DLC 第三次 | 同上 | 同上 | 同上 |
| 100040 | 本次移除业务模组 | 同上 | 同上 | 同上 |

故障指令读取 `[rcx+8]`。其直接调用点先从解析器对象读取一个字段，再加 `0x50` 后传入；由 `RCX=0x51` 可推导该字段为 `1`。

调用点的 token 分支和字段访问与 .NET 10 的 `ILStubResolver.ResolveToken` 匹配。上游把 `m_pCompileTimeState=1` 定义为已生成并释放 IL 的状态；正常 token 解析需要实际的编译数据。**因此“已释放状态仍被用于编译”是结合寄存器、实际机器指令和上游源码作出的强推断**，不是仅从异常名称猜测。参考 [.NET 10 状态定义](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/vm/ilstubresolver.h) 与 [解析/释放实现](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/vm/ilstubresolver.cpp)。

转储限制：标准异常上下文已经处于 FailFast 报告路径；原故障寄存器来自转储内保存的 AMD64 CONTEXT，并与异常地址、线程栈范围核对。原解析器堆页未包含在这些小转储中；没有将栈上地址扫描冒充完整 native 回溯，也没有直接观察释放者。游戏自带 CLR 没有匹配符号，函数语义由指令与上游源码对照推导。

### 3. 旧 MonoMod 实现与上游修复吻合

对实际 `0Harmony.dll` 的局部反编译表明：

- 包含 `Core100Runtime`，.NET 10 会选择它。栈中出现 `Core60Runtime` 是基类复用，不能解释成使用了 .NET 6 的运行时适配。
- `CompileMethodHook` 在首次进入时读写 `ThreadLocal<IAllocatedMemory>.Value`，为 JIT 分配包装结构。
- `ThreadLocal` 的初始化可能涉及系统锁及首次 JIT。JIT 钩子内再触发当前正在编译的原生调用存根，会形成重入窗口；仅有 `hookEntrancy` 计数不能使已被内层编译释放的数据重新有效。

MonoMod [PR #322](https://github.com/MonoMod/MonoMod/pull/322) 于 2026-09-29 合并，描述了同一个 `ILStubResolver` 状态变为 `1` 后继续解析的机制，并把钩子内的 ThreadLocal 包装缓存改为栈上局部结构。对应 [修复提交](https://github.com/MonoMod/MonoMod/commit/f16d7850f6706c77e077555c38e9dd1e39df2d33)。本机 Harmony 尚未包含这一实现变化。

上游复现入口是调试器下的 `StaticsHelpers` 原生调用，本机入口是 Waylith 工作线程上的 `SpinWaitInternal`，本轮未附加调试器。两者不是同一复现条件；匹配的是重入、编译状态释放和旧钩子实现，仍需本机修复构建对照。

## 启动对照结果

每次成对启动两个独立 Profile，观察 35 或 60 秒；有 RimTest 时还要求 `runtime.hello` 成功。无 RimTest 的“存活”只代表观察期内未退出。全部为全五 DLC，默认 28 处理器，除非表内另注。每个进程均由诊断脚本创建，结束后清理。

| 配置 | 结果 | 解释 |
| --- | --- | --- |
| 原版、英文，无 Harmony | 2/2 存活 | 阴性对照，样本不足以证明原版永久稳定 |
| 仅加 Harmony、英文 | 2/2 存活 | 不能证明安装其他补丁后仍稳定 |
| Harmony + RimTest、英文 | 2/2 hello 成功 | 阴性结果 |
| Harmony + ClashOfRim、英文，无 RimTest/桥 | 2/2 存活 | 阴性结果 |
| 完整模组栈、英文 | 10/10 hello 成功 | 包含首次 2 次及额外 8 次；偶发问题不会每次出现 |
| 完整模组栈、中/英文 zh-CN/fr-FR | 4/4 hello 成功 | 阴性结果 |
| Harmony + RimTest、中/英文 zh-CN/fr-FR | **1/4 相同 CLR 崩溃，3/4 hello 成功** | 业务模组不是复现前提 |
| 中/英文、处理器限制为 4 | 8/8 hello 成功 | RimTest 配置 4 次、完整栈 4 次；仅为缓解验证，不计作根因修复 |

Waylith 实际创建 `max(1, Environment.ProcessorCount - 1)` 个工作线程。因此本机默认是 **27 个**，设置处理器数为 4 后是 **3 个**；此前目录名中的 `four-workers` 并不是准确的引擎线程数。

另在游戏运行时副本中运行了不加载游戏业务的 Harmony + SpinWait/ConcurrentBag 探针：15 次进程正常退出，未得到确定性最小复现。初次准备时 3 次因 deps 配置缺少自包含运行时信息而未启动，保留但不计入有效试验。不能把这个尚未复现问题的探针当作修复回归门禁。

## 处理方向

1. 测试暂可继续只对子进程使用 `DOTNET_PROCESSOR_COUNT=4`，并在报告中记录。该值也会改变其他依赖处理器数量的并行策略，不能写成游戏引擎的专用开关或稳定性保证。
2. 根因修复优先验证**包含 MonoMod #322 及其后续修正的 Harmony 构建**，在隔离包中与当前包交替运行相同启动矩阵，再跑完整业务矩阵。截至本次查询，Harmony 最新公开预发布仍为 rc.3；单纯把项目编译引用从 rc.2 改成 rc.3 不会替换玩家实际加载的 Workshop DLL。[官方发布页](https://github.com/pardeike/Harmony/releases/tag/v3.0.0-rc.3)
3. 若必须做本地临时兼容，先实验在首个 Harmony 钩子安装前预热相关原生调用，再验证默认处理器下的重复启动。当前尚未验证这个办法，**不作为产品修复提交**。增加业务 `try/catch` 无法可靠恢复已损坏的 CLR 编译状态。
4. 后续启动门禁应记录有效处理器配置、运行时/Harmony 哈希和失败进程退出码，并单列 CLR 启动失败，避免把基础设施失败误记为业务功能失败。

## 可重做的本地分析与覆盖限制

- 矩阵脚本：`LocalOnly/startup-crash/Run-StartupMatrix.ps1`；调用记录及结果目录在机器证据索引。
- 转储分析：`LocalOnly/startup-crash/inspect_dump.py`；使用本地 minidump 解析库、PEReader/pefile 和指令解码，不执行转储中的代码。
- REA 管理程序集元数据证据：`ev_4bc3eef7a728dfa1e03b9b609f6d7090439f91d8de92dcae63eae28f8f66649f`，元数据展开超出工具预算，结果为部分覆盖。native 深度分析因未配置 provider 不可用；后续局部 IL/指令/转储结果属于普通本地工具证据，不冒称 REA 结果。
- 未修改产品代码、全局环境、Steam 安装、Workshop 包或冻结的 1.6 内容。没有为绕开崩溃增加业务重复防护；本次提交只记录调查及证据索引。
