using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static partial class RaidCommitCrashTests
{
    private static void RunBindings(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool guarded in new[] { false, true })
        {
            foreach (bool reopen in new[] { false, true }) WithBinding(factory, guarded, f => VerifyBoundCreation(f, reopen));
            foreach (bool pending in new[] { false, true }) WithBinding(factory, guarded, f => VerifyLegacyCreation(f, pending));
            foreach (string fault in new[] { "binding", "payload" }) WithBinding(factory, guarded, f => VerifyCreationConflict(f, fault));
            foreach (bool bytes in new[] { false, true }) WithBinding(factory, guarded, f => VerifyCreationRace(f, bytes));
            foreach (bool changed in new[] { false, true }) WithBinding(factory, guarded, f => VerifyFinalCreationReplay(f, changed));
        }
    }

    private static void WithBinding(Func<string, SaveSnapshotPackage> factory, bool guarded, Action<BindingFixture> run)
        => WithStorage((database, root) => run(new BindingFixture(database, root, factory, guarded)));

    private static void VerifyBoundCreation(BindingFixture f, bool reopen)
    {
        var original = f.Call(); Require(original.Result.Accepted, "Could not create bound raid.");
        if (reopen) f.Open();
        f.Store.Reset(); string before = f.Evidence();
        foreach (var (name, change) in RaidCreationMutations())
        {
            var rejected = f.Call(change);
            Require(!rejected.Result.Accepted && rejected.EventId is null && rejected.AppliedSnapshotId is null && rejected.NextLineageToken is null
                && before == f.Evidence() && f.Store.Writes == 0, "Changed raid creation replay was accepted: " + name);
        }
        var corrupt = f.Package.Payload.ToArray(); corrupt[^1] ^= 1;
        var bad = f.Call(bytes: corrupt);
        Require(!bad.Result.Accepted && bad.NextLineageToken is null && before == f.Evidence() && f.Store.Writes == 0,
            "Raid replay trusted metadata instead of actual bytes.");
        SameCreation(original, f.Call()); Require(before == f.Evidence(), "Exact raid retry changed persistent state.");
        f.State.Ledger.MarkDelivered(original.EventId!, "settled", DateTimeOffset.UtcNow);
        f.State.Ledger.MarkApplied(original.EventId!, "settled", DateTimeOffset.UtcNow);
        if (f.Guarded) SeedGuard(f.State.MercenaryGuards, "replacement");
        before = f.Evidence(); SameCreation(original, f.Call());
        Require(before == f.Evidence(), "Raid retry reopened completed work or consumed a replacement guard.");
        var newer = f.Package with { Envelope = f.Package.Envelope with { Identity = new("userA", "colonyA", "newer"), NextLineageToken = "newer-secret" } };
        f.Store.Inner.StoreLatest(newer, newer.Index, DateTimeOffset.UtcNow);
        before = f.Evidence(); var historical = f.Call();
        Require(historical.Result.Accepted && historical.EventId == original.EventId && historical.AppliedSnapshotId == original.AppliedSnapshotId
            && historical.NextLineageToken is null && before == f.Evidence(), "Historical raid retry returned newer credentials.");
    }

    private static IEnumerable<(string, Action<JsonObject>)> RaidCreationMutations()
    {
        foreach (string field in new[] { "PackageVersion", "OwnerId", "ColonyId", "SnapshotId", "RimWorldVersion", "PayloadEncoding",
            "OriginalSha256", "PayloadSha256", "PreviousSnapshotId", "LineageToken", "NextLineageToken", "SnapshotUploadKind" })
            yield return (field, root => root["ConfirmedSnapshot"]![field] = "changed");
        foreach (string field in new[] { "OriginalSaveBytes", "PayloadBytes", "GameTicks", "DefenderThreatPoints" })
            yield return (field, root => root["ConfirmedSnapshot"]![field] = 987654);
        foreach (string field in new[] { "RaidPreparationId", "TargetWorldObjectId", "TargetMapId", "DefenderSnapshotId", "OpponentKind", "GuardDeploymentId" })
            yield return (field, root => root["Raid"]![field] = "changed");
        yield return ("hostile", root => root["Raid"]!["IsHostile"] = false);
        yield return ("online", root => root["Raid"]!["DefenderOnline"] = true);
        yield return ("wealth", root => root["Raid"]!["DefenderWealth"] = 987654);
        yield return ("tile", root => root["Raid"]!["TargetTile"] = 987654);
        yield return ("cooldown", root => root["Raid"]!["DefenderRaidCooldownUntilUtc"] = "2099-01-01T00:00:00+00:00");
        yield return ("parent", root => root["Raid"]!["Attacker"]!["SnapshotId"] = "changed");
        yield return ("defender-parent", root => root["Raid"]!["Defender"]!["SnapshotId"] = "changed");
        yield return ("pawns", root => root["Raid"]!["PawnGlobalKeys"] = new JsonArray("changed"));
        yield return ("items", root => root["Raid"]!["CarriedThings"] = new JsonArray(new JsonObject
            { ["GlobalKey"] = "changed", ["DefName"] = "Silver", ["StackCount"] = 1 }));
    }

    private static void SameCreation(EventCreationResponse original, EventCreationResponse replay) => Require(replay.Result.Accepted
        && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.EventId == original.EventId
        && replay.AppliedSnapshotId == original.AppliedSnapshotId && replay.NextLineageToken == original.NextLineageToken,
        "Raid replay lost original confirmation.");

    private static void VerifyLegacyCreation(BindingFixture f, bool pending)
    {
        if (pending)
        {
            f.Seed(); string path = Directory.GetFiles(f.Journal, "*.json").Single();
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["Raid"]!["Raid"]!["Payload"]!.AsObject().Remove("CreationRequestHash");
            File.WriteAllText(path, json.ToJsonString());
        }
        else
        {
            Require(f.Call().Result.Accepted, "Could not create legacy fixture.");
            var raid = f.State.Ledger.FindByIdempotencyKey("raid")!;
            var payload = (RaidEventPayload)raid.Payload;
            f.State.Ledger.ApplyBatch(new LedgerBatch([], [new(raid.EventId, raid.Status, raid.Status, payload,
                payload with { CreationRequestHash = null }, raid)]));
        }
        f.Open(); var rejected = f.Call();
        Require(!rejected.Result.Accepted && rejected.EventId is null && rejected.NextLineageToken is null && !f.State.SnapshotCommits.HasPending
            && ((RaidEventPayload)f.State.Ledger.FindByIdempotencyKey("raid")!.Payload).CreationRequestHash is null
            && (!f.Guarded || f.State.MercenaryGuards.FindByIdempotencyKey("guard")!.Status == MercenaryGuardContractRegistry.StatusConsumed),
            "Legacy recovery lost the raid/guard or adopted a new request hash.");
        f.Open(); string before = f.Evidence(); Require(!f.Call().Result.Accepted && before == f.Evidence(), "Legacy retry rewrote state.");
    }

    private static void VerifyCreationConflict(BindingFixture f, string fault)
    {
        var intent = f.Seed(); f.Open();
        var raid = intent.Raid!.Raid; var payload = (RaidEventPayload)raid.Payload;
        var changed = raid with { Payload = fault == "binding" ? payload with { CreationRequestHash = "v1:other" }
            : payload with { AttackForce = payload.AttackForce! with { PawnGlobalKeys = ["other"] } } };
        f.State.Ledger.Append(changed);
        if (intent.Raid.GuardNotification is { } notice) f.State.Ledger.Append(notice);
        string before = f.Evidence(); bool rejected = false;
        try { f.State.SnapshotMutations.Recover(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected && before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending,
            "Recovery published a snapshot over conflicting raid content.");
    }

    private static void VerifyCreationRace(BindingFixture f, bool bytes)
    {
        Action<JsonObject> Change(int i) => root => root["Raid"]!["DefenderWealth"] = 100000 + i;
        var replies = SnapshotReplayRace.Run(i => Task.FromResult(bytes ? f.Call(bytes: i == 1 ? null : [(byte)i])
            : f.Call(Change(i))), () => { });
        Require(replies.Count(r => r.Result.Accepted) == 1 && f.Store.Writes == 1 && f.State.Ledger.ListByType(ServerEventType.Raid).Count == 1,
            "Different same-key raid requests shared a successful confirmation.");
        int winner = Array.FindIndex(replies, r => r.Result.Accepted);
        SameCreation(replies[winner], bytes ? f.Call() : f.Call(Change(winner + 1)));
        Require(replies.Where(r => !r.Result.Accepted).All(r => r.AppliedSnapshotId is null && r.NextLineageToken is null),
            "Rejected raid competitor received credentials.");
    }

    private static void VerifyFinalCreationReplay(BindingFixture f, bool changed)
    {
        var intent = f.Seed(); File.Delete(Directory.GetFiles(f.Journal, "*.json").Single()); f.Open();
        var prepared = f.Store.PreparePayload(intent.Package.Payload);
        EventCreationResponse? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(changed ? root => root["Raid"]!["DefenderWealth"] = 1 : null), () =>
            {
                var snapshot = new LatestSnapshotRecord(intent.Package.Envelope.Identity, intent.Package.Envelope, intent.Package.Index, intent.AcceptedAtUtc);
                f.State.SnapshotCommits.CommitRaid(f.Store, f.State.BankLoans, f.State.Ledger, f.State.MercenaryContracts, f.State.MercenaryGuards,
                    intent.Package, snapshot, intent.Raid!, (_, _) => { }, _ => { }, preparedStoragePayload: prepared);
            });
        Require(reply!.Result.Accepted != changed && (changed ? reply.NextLineageToken is null && reply.AppliedSnapshotId is null
            : reply.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && reply.EventId == intent.Raid!.Raid.EventId),
            "Final raid replay ignored original request binding.");
    }

    private sealed class BindingFixture
    {
        private readonly string database, root;
        public bool Guarded { get; }
        public SaveSnapshotPackage Package { get; }
        public string Journal => Path.Combine(root, "intents");
        public ClashOfRimNetworkState State = null!;
        public SnapshotRecoveryTestStore Store = null!;
        private string token = "";
        public BindingFixture(string database, string root, Func<string, SaveSnapshotPackage> factory, bool guarded)
        {
            this.database = database; this.root = root; Guarded = guarded; Open();
            Store.Gate = null; Package = Setup(State, factory, guarded); Store.Gate = State.RaidSettlementSnapshotMutationGate; Store.Reset();
        }
        public void Open()
        {
            Store = new(Path.Combine(root, "snapshots"));
            State = new(snapshotStore: Store, ledger: new SqliteAuthoritativeEventLedger(database), mercenaryGuards: Guards(database),
                snapshotCommits: new SnapshotCommitService(Journal), serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
            Store.Gate = State.RaidSettlementSnapshotMutationGate;
            State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out string session);
            token = State.AuthTokens.Issue("userA", "userA", "colonyA", session, DateTimeOffset.UtcNow);
            State.DiplomacyRelations.SetRelationKind("userA", "colonyA", "userB", "colonyB", DiplomacyRelationRegistry.RelationHostile, null, DateTimeOffset.UtcNow);
        }
        public EventCreationResponse Call(Action<JsonObject>? mutate = null, byte[]? bytes = null) => Invoke(State,
            bytes is null ? Package : Package with { Payload = bytes }, Guarded, token: token, mutate: mutate).GetAwaiter().GetResult();
        public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = State.Ledger.ListAll(),
            guard = State.MercenaryGuards.FindByIdempotencyKey("guard"), replacement = State.MercenaryGuards.FindByIdempotencyKey("replacement"),
            a = State.EventNotifications.GetVersion("userA"), b = State.EventNotifications.GetVersion("userB") });
        public SnapshotCommitIntent Seed()
        {
            Store.FailPublication = true;
            try { Call(); throw new Exception("Missing publication failure."); } catch (IOException) { }
            Store.FailPublication = false;
            return JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(Directory.GetFiles(Journal, "*.json").Single()))!;
        }
    }
}
