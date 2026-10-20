# 中立殖民地观察造成敌对：修复与定向验证

## 原因与改动

中立殖民地使用 `Scout` 远程地图模式。`RemoteObservationMapPostProcessor` 原先将“隐藏陷阱或启用防御 AI”当成了切换敌对关系的依据，直接修改目标玩家的全局代理阵营。它禁止当场发送敌对信件，但玩家列表同步随后恢复服务端的 Neutral 关系，原版因此发送注明原因为 `player-list-sync` 的关系回暖通知。

删除观察后处理中的强制 Hostile，以及友方观察中的强制 Ally。观察只负责投影居民、原版防守任务和可见性，不再改写玩家外交关系。正式袭击仍由现有 `ClientRaidBattlefield.PrepareDefender` 建立战斗敌对关系；不增加另一套外交规则。

中立地图的居民保持目标代理阵营的 Neutral 关系，使用 `LordJob_DefendBase` 和原版防守职责，不成为观察者可操控的殖民者。侦察模式继续隐藏陷阱并重新布置迷雾；友方观察继续保留原来的可见性。

同时纳入地面迷雾分类修正：`map.OrbitalDebris` 管理器在普通地面地图上也可能存在，不能凭其非空选择太空迷雾分支。保留生成器、地图父对象及边缘地形识别，地面地图使用原有边缘可达区域规则。

## 单元回归

新增 `RemoteObservationDiplomacyTests`，执行真实后处理入口，隔离无关的地图构造效果：

- Neutral / Ally / Hostile 三种既有关系；
- Scout / FriendlyObservation / RaidObservation 三种观察方式；
- 通过已有远程地图兼容测试在四种区域设置下运行，共 36 个组合；
- 检查双方关系和关系写入次数，防止同一轮中先改错再恢复掩盖问题；
- 检查侦察、战斗观察仍调用陷阱隐藏和迷雾处理。

修复前测试明确失败：`Observing Neutral owner in Scout changed global diplomacy.` 修复后通过。

相关单元测试组 **4/4 通过**：远程地图兼容、观察会话、袭击启动、RimTest 命令契约和准入。桥接命令契约增加独立观察查询和显式测试场景构造命令，普通远程会话轮询不额外扫描地图。

## RimTest 实测

使用 `Tools/RimTestObservationDiplomacy.json`，本地 RimWorld `1.7.4980 rev60`、全五个 DLC、英文、双客户端及独立服务端。只运行该定向配置和框架共用的入口、世界、存档及日志检查，未运行完整业务回归。

最终 **18/18 通过**，证据：

`G:/RimTestRuns/observation-diplomacy-2026-10-20/20261020-153441-3f82a7c8`

| 检查 | 结果 |
| --- | --- |
| 中立关系观察，重复打开和关闭两次 | 双方全程 Neutral |
| 每次观察推进 180 ticks | 居民保持 Neutral、原版防守 Lord 和职责、不可由观察者控制 |
| 观察期间主动刷新玩家列表 | 刷新前后关系一致 |
| 指定钢制尖刺陷阱 | Scout 中原物不可见，隐藏代理保留原陷阱 |
| 封闭有屋顶房间与开放地图边缘 | Scout 中房内有迷雾、开放边缘可见 |
| 经正常外交事件结盟后重复友方观察 | 保持 Ally，陷阱和原可见区域正常显示 |
| 关闭远程地图 | 本地地图数量、权威快照及关系保持正确 |
| 存档重载、会话重连和严格运行日志检查 | 通过 |

首轮 `20261020-152620-191c92c2` 在陷阱数量断言处失败：测试只主动构造一枚陷阱，但地图已有额外原版陷阱，实际有四个隐藏代理。保留失败报告；最终测试检查指定位置的测试陷阱，并要求所有普通陷阱在侦察中隐藏，不再假定整张地图只有一枚陷阱。

定向场景复用 `RemoteMapScenario` 的打开、关闭和状态核对流程。入口辅助程序先关闭原版落地叙述，再运行并等待会话，避免测试自身在强制暂停期间等待 tick 驱动的登录；这不是对另一个“首次建图持续无法联网”报告的修复。

实测使用当前工作区构建；`inputs.json` 保留程序集哈希和既有未提交修改。结果不等于已发布版本验证，也不覆盖第三方 AI 模组或所有太空地图生成器。游戏、日志、存档和二进制不提交。

复跑：

```powershell
$env:DOTNET_PROCESSOR_COUNT = '4'
./Tools/Test-RimTest.ps1 `
  -RimWorldPath 'E:/SteamLibrary/steamapps/common/RimWorld' `
  -HarmonyPath 'E:/SteamLibrary/steamapps/workshop/content/294100/2009463077' `
  -RunsDirectory 'G:/RimTestRuns/observation-diplomacy' `
  -Profile Tools/RimTestObservationDiplomacy.json
```
