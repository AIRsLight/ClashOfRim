# ClashOfRim 服务器

## 项目介绍

ClashOfRim 面向希望共享同一个 RimWorld 世界、但不想把游戏改造成确定性锁步模拟的社群。每个玩家仍然在本地运行正常的 RimWorld 殖民地。服务器提供的是围绕这些殖民地的持久共享层：身份、世界成员、存档快照、事件、经济、外交、袭击、聊天、管理和兼容性策略。

服务器对多人状态保持权威，但不接管 RimWorld 的逐 tick 游戏模拟。客户端在本地状态需要确认时上传存档快照。服务器记录这些快照，校验其顺序和兼容性，并将其作为结算、恢复、回档防护和事件完成的依据。

相较于 tick 同步联机，这套架构牺牲的是所有玩家同时控制同一确定性模拟的能力，换来更低耦合和更强的故障隔离。服务器不要求每个客户端逐 tick 模拟所有殖民地，因此离线玩家不会持续占用网络和模拟成本，断线也可以转为待处理账本工作。兼容性问题、投递失败、袭击结算异常或回档尝试都会被限制在快照和事件边界上，服务器可以校验、拒绝或修复状态，而不是让一次反同步污染整个会话。

大部分多人操作都表现为账本事件。交易、礼物、支援、外交、银行、佣兵、袭击和服务器通知都使用同一套事件与确认模型。在线玩家可以收到即时推送；不限定在线的流程则可以保留为待处理事件，等玩家重新连接后继续。

离线事件不会因为被下载就立刻视为已消费。玩家重新连接后，客户端会拉取待处理事件，并按处理方式分流。不需要玩家选择的事件可以批量自动应用，并在下一次快照上传时一起确认；需要玩家选择的事件会留在游戏内信件或事件收件箱中，直到玩家接受、拒绝、推迟或事件过期。仅在线通知仍保持在线限定，不会在玩家离线后重放为待处理事件。

会改变本地游戏状态的事件通过快照完成确认。客户端先在 RimWorld 中应用事件，然后上传一份带有已确认事件 ID 的存档快照。服务器会把这份快照与该玩家当前服务器状态进行校验，校验通过后才将事件标记为完成。若客户端无法提交有效确认快照，该事件会保持未完成，或通过明确的恢复路径失败，而不会被静默消费。

袭击也遵循快照优先的思路，但最终结算由服务器编辑存档完成。进攻方会在基于防守方存档生成的临时远程地图上战斗；进攻方提交的战斗快照只作为结算依据，不会直接替换防守方最新存档。服务器会将战斗结果与防守方权威快照对比，按服务器策略应用允许的物品、植物、建筑、载具、地面覆盖层和 pawn 损失，然后写出新的防守方快照。这样既能从实际战斗地图状态结算，又能让袭击损失受到服务器规则约束。

服务器还负责多人世界的运维状态。它保存管理员设置、维护封锁、封禁、账户数据、商店和经济设置、袭击策略、成就数据、兼容性基线和插件状态。服务器插件可以扩展存档索引、基线校验、结算和成就指标，而不需要把每个 DLC 或第三方模组规则都写死在核心服务器中。

## 部署指南

### 构建

如果 clone 仓库时没有拉取子模块，先初始化兼容包：

```powershell
git submodule update --init --recursive
```

在仓库根目录构建 Windows 服务端包：

```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\BuildWindowsServer.ps1
```

输出目录：

```text
Build\ClashOfRim.NetworkServer\win-x64
```

默认情况下，该脚本会同时构建并复制可用的服务器插件。只有在明确需要纯核心服务器包时，才使用 `-SkipThirdPartyCompat`。

### 安装

1. 将 `Build\ClashOfRim.NetworkServer\win-x64` 复制到服务器机器。
2. 在该目录中启动 `ClashOfRim.NetworkServer.exe`。
3. 首次启动时，如果 `appsettings.json` 不存在，服务器会自动创建。已有配置文件会被保留，只补全缺失字段。
4. 持久保存生成的 `Data/` 目录。该目录包含 `server.sqlite`、快照包、账户数据、世界状态、事件和运行时设置。
5. 调试服务器生命周期或客户端同步问题时，保留 `Logs/`。

部署时不要覆盖生产环境的 `Data/` 目录。发布包会刻意排除运行时数据和日志。

### 最小配置

`appsettings.json` 示例：

```json
{
  "Urls": "http://0.0.0.0:5000",
  "Localization": {
    "Language": "ChineseSimplified"
  },
  "Persistence": {
    "DataDirectory": "Data"
  },
  "Authentication": {
    "DebugMode": "false",
    "SteamAppId": "294100",
    "SteamWebApiKey": ""
  }
}
```

配置说明：

- `Urls` 控制监听地址和端口。
- 公开部署需要 HTTPS/WSS 时，建议使用反向代理终止 TLS。
- `Localization:Language` 控制 CLI 文本和服务器默认回退文本。若配置文件由服务器首次生成，默认语言会尽量根据操作系统语言检测。
- `Persistence:DataDirectory` 指向持久化服务器数据目录。
- `Authentication:DebugMode=true` 仅用于本地测试。
- 配置 `SteamWebApiKey` 时，服务器使用 Steam 票据校验。
- 未配置 Steam Web API key 时，服务器使用离线账户和密码登录。
- 首个完成服务器初始化的玩家会成为初始管理员。

## 致谢

ClashOfRim 在设计和实现过程中参考研究了多个 RimWorld 多人和模组项目：

- [RimWorld Together](https://github.com/RimWorld-Together/Rimworld-Together)：客户端/服务器多人流程、世界地图同步和事件流程研究的重要参考。
- [RimWorld Multiplayer](https://github.com/rwmt/Multiplayer)：严格兼容、清单、模组配置和同步设计经验的重要参考。
- [Harmony for RimWorld](https://github.com/pardeike/HarmonyRimWorld)：RimWorld 模组生态使用的 patch 基础，本项目也依赖 Harmony。
- 各类开源 RimWorld 模组和兼容项目，为存档加载、UI 行为、世界对象、载具、存储和 pawn 渲染提供了可研究案例。

ClashOfRim 是独立项目，不隶属于 Ludeon Studios 或上述项目。
