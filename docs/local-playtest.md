# 1.7 隔离实机验证

使用 PowerShell 7 在仓库根目录执行：

```powershell
./Tools/Start-IsolatedPlaytest.ps1 `
  -RimWorldPath 'E:/SteamLibrary/steamapps/common/RimWorld' `
  -HarmonyPath 'E:/SteamLibrary/steamapps/workshop/content/294100/2009463077' `
  -LaunchClients 2
```

脚本构建当前客户端，在 `LocalOnly/Playtests/<时间-随机标识>/` 创建独立运行目录，复制游戏根目录运行文件、当前模组包和 Harmony。两个客户端分别使用 `ClientA`、`ClientB` 保存配置、存档和缓存；日常存档与 Steam 安装目录中的模组不被覆盖。默认只启用 Core、Harmony、ClashOfRim，不启用 DLC 或第三方兼容包；已安装 DLC 标记为已知，避免游戏首次启动自动启用。

`-LaunchClients 0`（默认）只准备文件；`1` 或 `2` 启动对应数量的进程。`-QuickTest` 使用游戏自带快速建图入口，仅用于启动/资源加载排查。联机世界应按正常服务器进入流程创建，不能把两个独立 quicktest 世界当成共享世界验证。

`playtest.json` 记录游戏版本、来源路径、Git 提交/工作区状态、实际客户端 DLL 的 SHA256、配置目录及本次启动 PID/时间。启动成功不是验收通过。检查进程时同时核对路径和启动时间，避免 PID 重用后误操作其他进程。

`Game/Data` 和 `Game/Rendering` 是指向原游戏资产的目录联接，只用于读取；不要通过这些路径编辑资产或对运行目录盲目执行递归删除。脚本没有自动删除和停止进程功能。测试完成后退出这两个游戏进程，保留日志和必要存档供复现。

## 联机验收记录要求

使用仅绑定回环地址、独立数据目录的测试服务器及两个测试账户，记录服务器版本/配置、客户端清单、起止快照 ID 与 lineage、银行业务 ID/状态、事件 ID/状态及白银/物品实际变化。切服测试还需要第二个独立服务器数据目录。不要连接生产服务器执行故障测试。

| 场景 | 必须观察的结果 |
| --- | --- |
| 请求进行中退出并重新登录 | 旧 HTTP/WS 响应和排队主线程动作不能改变新会话；新会话可继续正常刷新 |
| 从测试服务器 A 切换到 B | A 的迟到响应不改变 B 的聊天、地图标记、业务状态或上传锁；B 的真实游戏状态正常 |
| 借款、贷款还款、债务还款 | 游戏白银与服务器记录、确认快照一致；响应丢失/服务重启后重试不重复经济效果 |
| 两客户端袭击与结算 | 出征 Pawn/物品、护卫队消耗、远程地图加载、结算差量和返回快照一致；重复提交不复制或丢失对象 |
| 保存并重新加载 | 游戏引擎可以重载确认后的真实存档，服务端继续认可其 lineage |

## 2026-10-14 观察

当前隔离启动已实际使用 RimWorld `1.7.4977 rev1248`、游戏自带 .NET `10.0.10` 和 Harmony `3.0.0.0-rc.2`。Core-only 配置日志确认 ClashOfRim 初始化并注册 128 个补丁，进入 `Game.InitNewGame`。修复前全 DLC 配置注册 130 个补丁，两者不应直接比较为补丁缺失。

首次启动发现 `ClashCommandIcons` 与 `MercenaryRecallShuttleLoadGizmoPatch` 的纹理字段被游戏报告缺少主线程初始化声明。图标集中到带 `[StaticConstructorOnStartup]` 的 `ClashCommandIcons` 后，第二次启动未出现这两条警告；20 项完整客户端测试通过。日志仍有环境 Vulkan layer manifest、未启用创意工坊模组元数据和 English 两条翻译错误的警告，未将整个启动声称为零警告。

可视交互未完成：窗口抓取为黑屏，窗口激活两次返回 `GetCursorPos failed: 拒绝访问 (0x80070005)`，用户确认当前为远程环境。不能据此认定游戏地图已可操作，上表双客户端场景全部保留待验证。原始日志位于本地 `LocalOnly/Playtests/20261014-121947-742faee3/`（修复前）与 `20261014-122140-29bcc988/`（修复后），不入库。
