using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

internal static class RaidLaunchModuleTests
{
    private static readonly ProtocolIdentity Attacker = new("userA", "colonyA", "before");
    private static readonly ProtocolIdentity Defender = new("userB", "colonyB", "defender");

    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var previous = CultureInfo.CurrentCulture;
        string root = Path.Combine(Path.GetTempPath(), "cor-raid-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Func<bool, EconomicModuleTestContext> fixture = guarded => Fixture(factory, root, guarded);
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (bool guarded in new[] { false, true }) PrepareAndStart(fixture, guarded);
                AdmissionChanges(fixture);
            }
            foreach (int phase in new[] { 1, 2, 3 }) RevokeDuringRecovery(fixture, phase);
            foreach (string change in new[] { "pvp", "wealth", "relation", "online", "map", "snapshot", "guard", "cooldown", "loss" })
                RevalidateAfterPreparation(fixture, change);
            ReconcileRevocation(fixture);
            MissingFields(fixture);
            var retired = (EventCreationResponse)((IValueHttpResult)RaidEndpoints.CreateRaid()).Value!;
            Check(!retired.Result.Accepted && retired.EventId is null, "Retired raid endpoint accepted a transaction.");
        }
        finally { CultureInfo.CurrentCulture = previous; Directory.Delete(root, true); }
    }

    private static EconomicModuleTestContext Fixture(Func<string, SaveSnapshotPackage> factory, string root, bool guarded)
    {
        var f = new EconomicModuleTestContext(factory, new FileColonySnapshotIndexStore(Path.Combine(root, Guid.NewGuid().ToString("N")))) { Configuration = new(pvpEnabled: true) };
        var defender = factory("defender");
        defender = defender with { Envelope = defender.Envelope with { Identity = new("userB", "colonyB", "defender") } };
        ((IColonySnapshotPackageStore)f.Snapshots).StoreLatest(defender, defender.Index, DateTimeOffset.UtcNow);
        f.Relations.SetRelationKind("userA", "colonyA", "userB", "colonyB", DiplomacyRelationRegistry.RelationHostile, null, DateTimeOffset.UtcNow);
        if (guarded) Guard(f);
        return f;
    }

    private static void Guard(EconomicModuleTestContext f) => f.MercenaryGuards.ActivateWithSnapshot(
        "guard", "userB", "colonyB", "guard", "defender", "Apprentice", 600, 0.35f, 1000, DateTimeOffset.UtcNow);

    private static PrepareRaidRequest Preparation(string map = "0") => new("raid", Attacker, Defender, false, "world", map, 1);
    private static CreateRaidWithSnapshotRequest Transaction(EconomicModuleTestContext f, string? preparationId = null,
        bool guarded = false, string map = "0", string key = "raid") => new(
        new CreateRaidRequest(key, Attacker, Defender, false, false, -100, null, preparationId, "world", map,
            "defender", ["pawn"], [], 1, "Player", guarded ? "guard" : null), ProtocolDtoMapper.ToMetadataDto(f.Package));

    private static void PrepareAndStart(Func<bool, EconomicModuleTestContext> fixture, bool guarded)
    {
        var f = fixture(guarded);
        f.Configuration = new(pvpEnabled: true, raidMaxDuration: TimeSpan.FromMinutes(8.5), raidTimeoutGracePeriod: TimeSpan.FromMinutes(3.25));
        var before = DateTimeOffset.UtcNow;
        var prepared = f.RaidPreparation.Prepare(Preparation("Map_0"));
        Check(prepared.Result.Accepted && prepared.DefenderSnapshotId == "defender"
            && prepared.DefenderPackage?.OwnerId == "userB" && prepared.ExpiresAtUtc >= before.AddMinutes(10)
            && prepared.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(10)
            && prepared.RaidMaxDurationMinutes == f.Configuration.RaidMaxDuration.TotalMinutes
            && prepared.RaidTimeoutGraceMinutes == f.Configuration.RaidTimeoutGracePeriod.TotalMinutes
            && f.Ledger.ListAll().Count == 0 && f.Recovery.Commits == 0,
            "Preparation lost its quote, lifetime, map normalization or mutated the ledger: " + prepared.Result.Message);
        Check(f.RaidPreparations.TryAuthorizeDownload(prepared.RaidPreparationId, "userA", "colonyA", "userB", "colonyB", before)
            && !f.RaidPreparations.TryAuthorizeDownload(prepared.RaidPreparationId, "userA", "other-colony", "userB", "colonyB", before),
            "Preparation did not bind download scope.");
        Check(guarded ? prepared.GuardDeployment is { ContractId: "guard", PriceSilver: 600, Points: >= 35 }
            : prepared.GuardDeployment is null, "Preparation lost guard deployment quote.");
        var transaction = Transaction(f, prepared.RaidPreparationId, guarded, "Map_0");
        var denied = f.RaidStarting.CreateWithSnapshot(transaction, f.Package.Payload, "forged");
        Check(!denied.Result.Accepted && f.Recovery.Replays == 0 && f.Raids.Reconciliations == 1,
            "Unauthenticated start triggered recovery/reconciliation.");
        var started = f.RaidStarting.CreateWithSnapshot(transaction, f.Package.Payload, f.Token);
        Check(started.Result.Accepted && started.EventId == prepared.RaidEventId && started.AppliedSnapshotId == "paid"
            && started.NextLineageToken == f.Snapshots.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken
            && f.Recovery.Commits == 1, "Independent start failed: " + started.Result.Message);
        var source = f.Ledger.Find(started.EventId!)!;
        var payload = (RaidEventPayload)source.Payload;
        Check(payload.AttackForce?.AttackerSnapshotId == "paid" && payload.CreationRequestHash is not null
            && source.TargetContext?.MapUniqueId == "Map_0" && payload.StartedAtUtc == source.CreatedAtUtc,
            "Start lost confirmed force, binding or target map.");
        Check(started.RaidStartedAtUtc == source.CreatedAtUtc
            && started.RaidDeadlineUtc == source.CreatedAtUtc.AddMinutes(8.5)
            && started.RaidFinalDeadlineUtc == source.CreatedAtUtc.AddMinutes(11.75),
            "Start lost current policy deadlines or rounded fractional minutes.");
        Check(!guarded || f.MercenaryGuards.FindByIdempotencyKey("guard")!.ConsumedRaidEventId == source.EventId,
            "Confirmed start did not consume quoted guard.");
        Check(!f.RaidPreparations.TryAuthorizeDownload(prepared.RaidPreparationId, "userA", "colonyA", "userB", "colonyB", before.AddMinutes(11)),
            "Preparation did not expire its download capability.");
        string evidence = JsonSerializer.Serialize(f.Ledger.ListAll());
        f.Configuration = new(pvpEnabled: false);
        var replay = f.RaidStarting.CreateWithSnapshot(transaction, f.Package.Payload, f.Token);
        Check(replay.Result.Accepted && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && replay.EventId == source.EventId && replay.AppliedSnapshotId == started.AppliedSnapshotId
            && replay.NextLineageToken == started.NextLineageToken && f.Recovery.Commits == 1
            && evidence == JsonSerializer.Serialize(f.Ledger.ListAll()), "Disabled PvP rejected or recommitted the exact receipt.");
    }

    private static void AdmissionChanges(Func<bool, EconomicModuleTestContext> fixture)
    {
        foreach (string change in new[] { "pvp", "wealth", "relation", "online", "map", "cooldown", "loss" })
        {
            var f = fixture(false);
            if (change != "map") Change(f, change);
            string map = change == "map" ? "missing-map" : "0";
            var prepared = f.RaidPreparation.Prepare(Preparation(map));
            var started = f.RaidStarting.CreateWithSnapshot(Transaction(f, map: map), f.Package.Payload, f.Token);
            Check(!prepared.Result.Accepted && !started.Result.Accepted && prepared.Result.Message == started.Result.Message
                && f.Recovery.Commits == 0 && f.Snapshots.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before",
                "Preparation/start admission drifted for " + change);
            if (change == "loss") Check(prepared.RaidEventId == "loss" && started.EventId == "loss"
                && started.DeliverySemantics == ProtocolDeliverySemantics.ServerNotification, "Blocked attacker lost loss receipt identity.");
        }
    }

    private static void RevokeDuringRecovery(Func<bool, EconomicModuleTestContext> fixture, int phase)
    {
        var f = fixture(false); int calls = 0;
        f.Recovery.OnReplay = () => { if (++calls == phase) f.Tokens.RevokeForUser("userA"); };
        var response = f.RaidStarting.CreateWithSnapshot(Transaction(f), f.Package.Payload, f.Token);
        Check(response.Result.ErrorCode == ProtocolErrorCode.Unauthorized && calls == phase
            && f.Recovery.Commits == 0 && f.Ledger.ListAll().Count == 0
            && f.Snapshots.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before", "Recovery ignored revoked credentials: " + phase);
    }

    private static void ReconcileRevocation(Func<bool, EconomicModuleTestContext> fixture)
    {
        var f = fixture(false);
        f.Raids.OnReconcile = () => f.Tokens.RevokeForUser("userA");
        Check(f.RaidStarting.CreateWithSnapshot(Transaction(f), f.Package.Payload, f.Token).Result.ErrorCode == ProtocolErrorCode.Unauthorized
            && f.Recovery.Commits == 0 && f.Ledger.ListAll().Count == 0, "Timeout reconciliation bypassed reauthorization.");
    }

    private static void RevalidateAfterPreparation(Func<bool, EconomicModuleTestContext> fixture, string change)
    {
        var f = fixture(false); int calls = 0;
        f.Recovery.OnReplay = () => { if (++calls == 3) Change(f, change); };
        var result = f.RaidStarting.CreateWithSnapshot(Transaction(f), f.Package.Payload, f.Token);
        Check(!result.Result.Accepted && calls == 3 && f.Recovery.Commits == 0
            && f.Snapshots.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before"
            && f.Ledger.ListByTypeForActor(ServerEventType.Raid, "userA", "colonyA").Count == 0
            && (change != "guard" || f.MercenaryGuards.FindActiveForColony("userB", "colonyB")?.ContractId == "guard"),
            "Prepared upload skipped final admission or consumed guard: " + change);
    }

    private static void Change(EconomicModuleTestContext f, string change)
    {
        switch (change)
        {
            case "pvp": f.Configuration = new(pvpEnabled: false); break;
            case "wealth": f.Configuration = new(pvpEnabled: true, raidMinimumDefenderWealth: int.MaxValue); break;
            case "relation": f.Relations.SetRelationKind("userA", "colonyA", "userB", "colonyB", DiplomacyRelationRegistry.RelationNeutral, null, DateTimeOffset.UtcNow); break;
            case "online": f.Presence.TryConnectExclusive("userB", out _); break;
            case "guard": Guard(f); break;
            case "snapshot": case "map":
                var current = ((IColonySnapshotPackageStore)f.Snapshots).GetLatestPackage("userB", "colonyB")!;
                var updated = change == "map" ? current with { Index = current.Index with { Maps = [] } }
                    : current with { Envelope = current.Envelope with { Identity = current.Envelope.Identity with { SnapshotId = "changed" } } };
                ((IColonySnapshotPackageStore)f.Snapshots).StoreLatest(updated, updated.Index, DateTimeOffset.UtcNow);
                break;
            case "cooldown": case "loss":
                var force = new RaidAttackForceRecord("prior", ["pawn"], []);
                var payload = new RaidEventPayload("defender", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, force,
                    AttackerLoss: change == "loss" ? RaidAttackerLossRecord.FromAttackForce("prior", force, "timeout") : null);
                var evt = AuthoritativeEventFactory.Create(ServerEventType.Raid, new("server"),
                    change == "loss" ? new("userA", "colonyA") : new("userB", "colonyB"), "prior", false, payload, DateTimeOffset.UtcNow);
                f.Ledger.Append(evt with { EventId = change });
                break;
        }
    }

    private static void MissingFields(Func<bool, EconomicModuleTestContext> fixture)
    {
        var f = fixture(false);
        var missing = f.RaidStarting.CreateWithSnapshot(new(null!, null!), [], f.Token);
        Check(missing.Result.ErrorCode == ProtocolErrorCode.ValidationFailed && f.Recovery.Replays == 0 && f.Raids.Reconciliations == 0,
            "Missing transaction triggered work before validation.");
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
