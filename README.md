# ClashOfRim Server

## Project Introduction

ClashOfRim is designed for RimWorld communities that want a shared world without
turning the game into a deterministic lockstep simulation. Each player still
runs a normal RimWorld colony locally. The server provides the durable shared
layer around those colonies: identity, world membership, snapshots, events,
economy, diplomacy, raids, chat, administration, and compatibility policy.

Players on the same server are strongly encouraged to use the same RimWorld
language. Cross-language worlds can still expose inconsistent generated labels,
tile names, colony markers, ideology names, or mod-authored text, especially
around world creation, colony founding, remote maps, and pawn transfer.

The server is intentionally authoritative over multiplayer state, but not over
RimWorld's moment-to-moment simulation. Clients upload save snapshots when local
state changes need to be confirmed. The server records those snapshots, checks
their order and compatibility, and uses them as evidence for settlement,
recovery, rollback protection, and event completion.

Compared with tick-synchronized multiplayer, this architecture trades strict
shared control for lower coupling and better fault isolation. It does not need
every client to simulate every colony tick in lockstep, so inactive players do
not continuously consume network or simulation capacity, and disconnects can be
handled as pending ledger work. Compatibility problems, failed deliveries, raid
settlement errors, and rollback attempts are contained at snapshot and event
boundaries, where the server can validate, reject, or repair state instead of
letting a desync corrupt the whole session.

Most multiplayer actions are represented as ledger events. Trades, gifts,
support, diplomacy, bank operations, mercenary contracts, raids, and server
notifications all move through the same event-and-confirmation model. Online
players can receive immediate pushed events, while non-online-only workflows can
remain pending until the player reconnects.

Offline events are not consumed merely because they were downloaded. When a
player reconnects, the client fetches pending events and separates them by
handling mode. Events that do not require a player choice can be applied
automatically in one batch and confirmed together with the next snapshot upload.
Events that require a choice stay in the in-game letter or event inbox until the
player accepts, rejects, postpones, or the event expires. Online-only
notifications remain online-only and are not replayed as offline work.

Events that change local game state are completed by snapshot confirmation. The
client first applies the event in RimWorld, then uploads a save snapshot that
includes the confirmed event ids. The server validates that snapshot against the
player's current server state before marking the event complete. If the client
cannot produce a valid confirmation snapshot, the event remains unresolved or is
failed through an explicit recovery path instead of being silently consumed.

Raids use the same snapshot-first philosophy, but settlement is intentionally
server-edited. An attacker fights on a temporary remote map built from the
defender's save. The attacker's battle snapshot is treated as settlement
evidence, not as the defender's new save. The server compares the battle result
with the defender's authoritative snapshot, applies allowed item, plant,
building, vehicle, terrain-overlay, and pawn-loss consequences, and writes a new
defender snapshot. This keeps raid losses bounded by server policy while still
allowing the battle to be resolved from the actual saved map state.

The server also owns the operational side of a multiplayer world. It stores
administrator settings, maintenance locks, bans, account data, shop and economy
settings, raid policy, achievement data, compatibility baselines, and plugin
state. Server plugins can extend indexing, baseline validation, settlement, and
achievement metrics without embedding every DLC or third-party mod rule in the
core server.

## Deployment Guide

### Build

If you cloned the repository without submodules, initialize the compatibility
package first:

```powershell
git submodule update --init --recursive
```

Build a Windows server package from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\BuildWindowsServer.ps1
```

The output is written to:

```text
Build\ClashOfRim.NetworkServer\win-x64
```

By default, this script also builds and copies available server plugins into the
server package. Use `-SkipThirdPartyCompat` only when you intentionally want a
core-only server package.

### Install

1. Copy `Build\ClashOfRim.NetworkServer\win-x64` to the server machine.
2. Start `ClashOfRim.NetworkServer.exe` from that directory.
3. On first start, the server creates `appsettings.json` if it does not already
   exist. Existing configuration files are preserved and missing fields are
   filled in.
4. Keep the generated `Data/` directory persistent. It contains `server.sqlite`,
   snapshot packages, account data, world state, events, and runtime settings.
5. Keep `Logs/` when debugging server lifecycle or client synchronization
   problems.

Do not overwrite a production `Data/` directory during deployment. Release
builds intentionally exclude runtime data and logs.

### Minimal Configuration

Example `appsettings.json`:

```json
{
  "Urls": "http://0.0.0.0:5000",
  "Localization": {
    "Language": "English"
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

Configuration notes:

- `Urls` controls the listening address and port.
- Use a reverse proxy for HTTPS and WSS termination in public deployments.
- `Localization:Language` controls CLI text and server fallback text. If the
  server creates the config file, it tries to detect the operating-system
  language.
- `Persistence:DataDirectory` points to the persistent server data directory.
- `Authentication:DebugMode=true` is only for local testing.
- If `SteamWebApiKey` is configured, Steam ticket validation is used.
- If no Steam Web API key is configured, the server uses offline account and
  password login.
- The first player who completes initial server setup becomes the initial
  administrator.

## Acknowledgements

ClashOfRim was developed with research and comparison against existing RimWorld
multiplayer and modding projects:

- [RimWorld Together](https://github.com/RimWorld-Together/Rimworld-Together)
  for client/server multiplayer flow, world-map synchronization, and event
  workflow research.
- [RimWorld Multiplayer](https://github.com/rwmt/Multiplayer) for strict
  compatibility, manifest, mod configuration, and synchronization design
  lessons.
- [Harmony for RimWorld](https://github.com/pardeike/HarmonyRimWorld) for the
  patching foundation used across the RimWorld mod ecosystem.
- Open-source RimWorld mods and compatibility projects used as references for
  save loading, UI behavior, world objects, vehicles, storage, and pawn
  rendering.

ClashOfRim is an independent project and is not affiliated with Ludeon Studios
or the projects listed above.
