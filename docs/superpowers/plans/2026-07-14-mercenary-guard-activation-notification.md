# Mercenary Guard Activation Notification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver one persistent informational letter to a defending player when their mercenary guard contract is consumed by a player raid.

**Architecture:** A focused server-side factory creates the deterministic notification event from a consumed contract and raid event. The raid start endpoint appends it only after `ConsumeForRaid` succeeds, logs the append through existing diagnostics, and signals the defender only for a newly created event.

**Tech Stack:** C# 10, .NET 8 server, ClashOfRim authoritative event ledger, JSON server localization, NetworkSmoke executable tests.

## Global Constraints

- The notification is persistent and must be deliverable after the defender next logs in.
- The notification is informational, requires no confirmation, and does not affect raid settlement.
- Notification identity is deterministic per raid event so retries cannot create duplicate letters.
- Existing raid creation, guard deployment, and settlement behavior must remain unchanged.

---

### Task 1: Create and append the guard activation notification

**Files:**
- Create: `Source/Server/ClashOfRim.Network/Server/Services/MercenaryGuardActivationNotificationFactory.cs`
- Modify: `Source/Server/ClashOfRim.Network/Server/Endpoints/ClashOfRimNetworkServer.RaidsDiplomacySupport.cs:393-409`
- Modify: `Tools/ClashOfRim.NetworkSmoke/Program.cs:1-40`
- Modify: `Tools/ClashOfRim.NetworkServer/Localization/ChineseSimplified.json`
- Modify: `Tools/ClashOfRim.NetworkServer/Localization/English.json`
- Modify: `Tools/ClashOfRim.NetworkServer/Localization/Japanese.json`
- Modify: `Tools/ClashOfRim.NetworkServer/Localization/Korean.json`
- Modify: `Tools/ClashOfRim.NetworkServer/Localization/Russian.json`

**Interfaces:**
- Consumes: `MercenaryGuardContractRecord?`, `AuthoritativeEvent raidEvent`, defender online state, localized title/message, and `DateTimeOffset nowUtc`.
- Produces: `AuthoritativeEvent? MercenaryGuardActivationNotificationFactory.Create(...)`; returns `null` when no contract was consumed.

- [ ] **Step 1: Write the failing NetworkSmoke test**

Add `VerifyMercenaryGuardActivationNotificationSemantics()` before the `--snapshot-pipeline-only` cutoff. The test creates a consumed guard record and raid event, calls the missing factory, and asserts:

```csharp
AuthoritativeEvent? notification = MercenaryGuardActivationNotificationFactory.Create(
    consumedGuard,
    raidEvent,
    false,
    "Guard team deployed",
    "The guard team has deployed.",
    now);
Require(notification is not null, "已消费护卫合同应创建通知");
Require(notification!.Type == ServerEventType.ServerNotification, "护卫通知应使用服务器通知类型");
Require(notification.Target.UserId == "defender" && notification.Target.ColonyId == "colony-defender", "护卫通知目标应为防守方");
var payload = (ServerNotificationEventPayload)notification.Payload;
Require(!payload.OnlineOnly, "护卫通知必须支持离线投递");
Require(payload.RelatedEventId == raidEvent.EventId && payload.RelatedEventType == ServerEventType.Raid, "护卫通知应关联袭击事件");

var ledger = new InMemoryAuthoritativeEventLedger();
Require(ledger.Append(notification).Created, "首次追加护卫通知应成功");
Require(!ledger.Append(notification).Created, "重复追加同一护卫通知应幂等");
Require(
    MercenaryGuardActivationNotificationFactory.Create(null, raidEvent, false, "title", "message", now) is null,
    "合同未消费时不得创建护卫通知");
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet run --project Tools\ClashOfRim.NetworkSmoke\ClashOfRim.NetworkSmoke.csproj --no-restore -- --snapshot-pipeline-only
```

Expected: build fails because `MercenaryGuardActivationNotificationFactory` does not exist.

- [ ] **Step 3: Implement the minimal event factory**

Create `MercenaryGuardActivationNotificationFactory` with this behavior:

```csharp
public static AuthoritativeEvent? Create(
    MercenaryGuardContractRecord? consumedContract,
    AuthoritativeEvent raidEvent,
    bool targetOnline,
    string title,
    string message,
    DateTimeOffset nowUtc)
{
    if (consumedContract is null)
    {
        return null;
    }

    string notificationId = "mercenary-guard-activated:" + raidEvent.EventId;
    return AuthoritativeEventFactory.Create(
        ServerEventType.ServerNotification,
        new EventParty("server"),
        new EventParty(consumedContract.UserId, consumedContract.ColonyId),
        notificationId,
        targetOnline,
        new ServerNotificationEventPayload(
            notificationId,
            title,
            message,
            ServerNotificationSeverity.Info,
            FromAdministrator: false,
            OnlineOnly: false,
            RelatedEventId: raidEvent.EventId,
            RelatedEventType: ServerEventType.Raid,
            RelatedUserId: raidEvent.Actor.UserId,
            RelatedColonyId: raidEvent.Actor.ColonyId),
        nowUtc);
}
```

- [ ] **Step 4: Integrate only after successful contract consumption**

Capture the return value from `ConsumeForRaid`. If non-null, create the notification with `state.OnlinePresence.IsUserOnline(...)` and localization keys `Mercenary.GuardDeployedTitle` and `Mercenary.GuardDeployedMessage`, append it, call `LogEventAppend(state, append, "mercenary-guard-activated")`, and call `state.EventNotifications.SignalUser(...)` only when `append.Created` is true.

- [ ] **Step 5: Add all server translations**

Add these two keys to every server localization JSON file:

```json
"Mercenary.GuardDeployedTitle": "Guard team deployed",
"Mercenary.GuardDeployedMessage": "Your hired guard team has deployed to defend against this raid. The contract has been fulfilled."
```

Use equivalent Chinese, Japanese, Korean, and Russian translations while retaining identical keys.

- [ ] **Step 6: Run targeted and build verification**

Run:

```powershell
dotnet run --project Tools\ClashOfRim.NetworkSmoke\ClashOfRim.NetworkSmoke.csproj --no-restore -- --snapshot-pipeline-only
dotnet build Tools\ClashOfRim.NetworkServer\ClashOfRim.NetworkServer.csproj --no-restore -v:minimal
```

Expected: targeted smoke tests pass and the server builds with zero errors and zero warnings.

- [ ] **Step 7: Commit**

```powershell
git add -- Source/Server/ClashOfRim.Network/Server/Services/MercenaryGuardActivationNotificationFactory.cs Source/Server/ClashOfRim.Network/Server/Endpoints/ClashOfRimNetworkServer.RaidsDiplomacySupport.cs Tools/ClashOfRim.NetworkSmoke/Program.cs Tools/ClashOfRim.NetworkServer/Localization/*.json
git commit -m "Notify defenders when guard teams deploy"
```
