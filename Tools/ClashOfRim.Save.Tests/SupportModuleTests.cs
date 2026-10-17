using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Support;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

internal static class SupportModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (string kind in new[] { "return", "loss", "reject" }) Lifecycle(factory, kind);
                SnapshotDeath(factory);
                foreach (bool returning in new[] { false, true }) Confirmation(factory, returning);
            }
            foreach (int phase in new[] { 1, 2 }) RevokedDuringRecovery(factory, phase);
            foreach (string kind in new[] { "return", "loss", "reject" }) ClosureRevoked(factory, kind);
            foreach (string change in new[] { "target-map", "source-base" }) RecoveredState(factory, change);
            foreach (int days in new[] { 2, 3, 30, 31 }) Duration(factory, days);
            DeletedActors(factory);
            var retired = (EventCreationResponse)((IValueHttpResult)SupportEndpoints.CreateSupportPawn()).Value!;
            Check(!retired.Result.Accepted && retired.EventId is null, "Retired support endpoint accepted a transaction.");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static EconomicModuleTestContext Fixture(Func<string, SaveSnapshotPackage> factory)
    {
        var f = new EconomicModuleTestContext(factory);
        var identity = new SnapshotIdentity("userB", "colonyB", "target");
        f.Snapshots.StoreLatest(new(identity, f.Package.Envelope with { Identity = identity }, f.Package.Index, DateTimeOffset.UnixEpoch));
        return f;
    }

    private static CreateSupportPawnWithSnapshotRequest Request(EconomicModuleTestContext f) =>
        SupportCommitTests.Request(f.Package with { Envelope = f.Package.Envelope with
        { PreviousSnapshotId = "before", LineageToken = f.Snapshots.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken } });

    private static EventCreationResponse Send(EconomicModuleTestContext f, CreateSupportPawnWithSnapshotRequest? request = null,
        string? token = null) => f.SupportSending.CreateWithSnapshot(request ?? Request(f), f.Package.Payload, token ?? f.Token);

    private static void Lifecycle(Func<string, SaveSnapshotPackage> factory, string kind)
    {
        var f = Fixture(factory);
        var request = Request(f);
        Check(!Send(f, request, "forged").Result.Accepted && f.Recovery.Replays == 0, "Unauthorized sender recovered pending work.");
        var created = Send(f, request);
        Check(created.Result.Accepted && created.AppliedSnapshotId == "paid" && f.Recovery.Commits == 1,
            "Independent support send failed: " + created.Result.Message);
        var source = f.Ledger.Find(created.EventId!)!;
        var payload = (SupportPawnEventPayload)source.Payload;
        string targetToken = f.Issue("userB", "colonyB");
        Check(!Close(f, kind, source, request, f.Token).Accepted && f.Ledger.ListAll().Count == 1,
            "Sender credentials closed support owned by its recipient.");
        var first = Close(f, kind, source, request, targetToken);
        Check(first.Accepted && first.Created, "Independent closure failed: " + kind);
        var result = f.Ledger.Find(first.Id!)!;
        var returned = (SupportPawnEventPayload)result.Payload;
        Check(result.Actor == source.Target && result.Target == source.Actor && returned.SourceEventId == source.EventId
            && returned.SourceSnapshotId == payload.SourceSnapshotId && returned.PawnGlobalKey == payload.PawnGlobalKey
            && returned.SourceTile == payload.SourceTile && returned.SourceCaravanLoadId == payload.SourceCaravanLoadId
            && returned.PermanentSupport == payload.PermanentSupport && returned.SupportDurationDays == payload.SupportDurationDays
            && returned.ExpiresAtGameTicks == payload.ExpiresAtGameTicks && returned.AutoReturnOnSettlement == payload.AutoReturnOnSettlement
            && returned.ReturnToSender && !returned.TemporaryControl && returned.ExpectedReturnAtUtc is null
            && returned.CreationRequestHash is null && result.TargetContext?.Tile == payload.SourceTile,
            "Shared support return lost its source, destination or lifetime metadata.");
        Check(kind == "loss" ? returned.PawnPackage is null && returned.PawnReference?.Dead == true
            : returned.PawnPackage?.Scribe?.Xml == payload.PawnPackage?.Scribe?.Xml,
            "Return package/death semantics changed: " + kind);
        Check(returned.ReturnReason == (kind == "reject" ? "Rejected" : "Finished:测试")
            && returned.RejectionReason == (kind == "reject" ? "拒绝:reason" : null), "Closure reason changed.");
        f.Ledger.ChangeStatus(result.EventId, ServerEventStatus.AppliedToSnapshot);
        string before = JsonSerializer.Serialize(f.Ledger.ListAll());
        long version = f.Notifications.GetVersion("userA");
        var replay = Close(f, kind, source, request, targetToken);
        var sentAgain = Send(f, request);
        Check(replay.Accepted && !replay.Created && replay.Id == first.Id && sentAgain.Result.Accepted
            && sentAgain.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && sentAgain.EventId == source.EventId
            && before == JsonSerializer.Serialize(f.Ledger.ListAll()) && f.Notifications.GetVersion("userA") > version
            && f.Recovery.Commits == 1, "Consumed support replay rewrote state or lost notification.");
    }

    private static (bool Accepted, bool Created, string? Id) Close(EconomicModuleTestContext f, string kind,
        AuthoritativeEvent source, CreateSupportPawnWithSnapshotRequest request, string token)
    {
        if (kind == "reject")
        {
            var response = f.Support.RejectSupportPawn(new(source.EventId, "userB", "colonyB", "target", "拒绝:reason", token));
            return (response.Result.Accepted, response.ReturnEventCreated, response.ReturnEventId);
        }
        var finish = f.Support.FinishSupportPawn(new("finish", source.EventId, "userB", "colonyB", "target", "Finished:测试",
            request.SupportPawn.PawnGlobalKey, "Returned pawn", kind == "loss", kind == "loss" ? null : request.SupportPawn.PawnPackage, token));
        return (finish.Result.Accepted, finish.Created, finish.ReturnEventId);
    }

    private static void RevokedDuringRecovery(Func<string, SaveSnapshotPackage> factory, int phase)
    {
        var f = Fixture(factory); int calls = 0;
        f.Recovery.OnReplay = () => { if (++calls == phase) f.Tokens.RevokeForUser("userA"); };
        Check(!Send(f).Result.Accepted && calls == phase && f.Recovery.Commits == 0 && f.Ledger.ListAll().Count == 0
            && f.Snapshots.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before", "Support send ignored recovery revocation.");
    }

    private static void ClosureRevoked(Func<string, SaveSnapshotPackage> factory, string kind)
    {
        var f = Fixture(factory); var request = Request(f); var created = Send(f, request);
        var source = f.Ledger.Find(created.EventId!)!; string token = f.Issue("userB", "colonyB");
        f.Recovery.OnReplay = () => f.Tokens.RevokeForUser("userB");
        Check(!Close(f, kind, source, request, token).Accepted && f.Ledger.ListAll().Count == 1
            && f.Notifications.GetVersion("userA") == 0, "Closure ignored authority revoked during recovery.");
    }

    private static void RecoveredState(Func<string, SaveSnapshotPackage> factory, string change)
    {
        var f = Fixture(factory); int calls = 0;
        var request = Change(Request(f), "TargetContext", null);
        f.Recovery.OnReplay = () =>
        {
            if (++calls != 2) return;
            if (change == "target-map")
            {
                var target = f.Snapshots.GetLatest("userB", "colonyB")!;
                f.Snapshots.StoreLatest(target with { Index = target.Index with { Maps = [] } });
            }
            else
            {
                var current = f.Snapshots.GetLatest("userA", "colonyA")!;
                f.Snapshots.StoreLatest(current with { Identity = current.Identity with { SnapshotId = "new-base" } });
            }
        };
        Check(!Send(f, request).Result.Accepted && calls == 2 && f.Recovery.Commits == 0 && f.Ledger.ListAll().Count == 0,
            "Support send did not revalidate " + change + " after preparation.");
    }

    private static void Duration(Func<string, SaveSnapshotPackage> factory, int days)
    {
        var f = Fixture(factory);
        var request = Change(Request(f), "SupportDurationDays", JsonValue.Create(days));
        Check(Send(f, request).Result.Accepted == (days is >= 3 and <= 30), "Temporary support duration boundary changed.");
        if (days is >= 3 and <= 30) return;
        Check(f.Recovery.Replays == 0, "Invalid duration entered recovery.");
        request = Change(request, "PermanentSupport", JsonValue.Create(true));
        Check(Send(f, request).Result.Accepted, "Permanent support unexpectedly requires temporary duration.");
    }

    private static void SnapshotDeath(Func<string, SaveSnapshotPackage> factory)
    {
        var f = Fixture(factory); var created = Send(f);
        var target = f.Snapshots.GetLatest("userB", "colonyB")!;
        var pawn = new PawnSummary("Pawn_42", "snapshot:target/pawn:Pawn_42", null, null, "Human", null, "Lost pawn", true, null, null);
        var upload = SnapshotUploadResult.Accept(target with { Index = target.Index with { Pawns = [pawn] } });
        f.SupportDeaths.Process(upload);
        Check(f.Ledger.Find(created.EventId!)!.Status == ServerEventStatus.Failed && f.Ledger.ListAll().Count == 2,
            "Independent support death reconciliation did not close the source.");
        string before = JsonSerializer.Serialize(f.Ledger.ListAll());
        f.SupportDeaths.Process(upload);
        Check(before == JsonSerializer.Serialize(f.Ledger.ListAll()) && f.Notifications.GetVersion("userA") == 2,
            "Snapshot death replay changed its receipt or missed the wake-up.");
    }

    private static CreateSupportPawnWithSnapshotRequest Change(CreateSupportPawnWithSnapshotRequest request, string name, JsonNode? value)
    {
        var json = JsonSerializer.SerializeToNode(request)!;
        json["SupportPawn"]![name] = value;
        return json.Deserialize<CreateSupportPawnWithSnapshotRequest>()!;
    }

    private static void Confirmation(Func<string, SaveSnapshotPackage> factory, bool returning)
    {
        var f = Fixture(factory); var created = Send(f); var source = f.Ledger.Find(created.EventId!)!;
        if (returning)
        {
            source = SupportReturns.CreateRejectedSupportPawnReturnEvent(source, (SupportPawnEventPayload)source.Payload,
                "reason", false, DateTimeOffset.UnixEpoch);
            f.Ledger.Append(source);
        }
        var payload = (SupportPawnEventPayload)source.Payload;
        var current = f.Snapshots.GetLatest(source.Target.UserId, source.Target.ColonyId!)!;
        var snapshot = current with { Identity = current.Identity with { SnapshotId = "confirmed" } };
        ConfirmEventApplicationResultDto Confirm(string user, string result) => SupportApplicationConfirmation.Confirm(
            new(source.EventId, null, result), user, source.Target.ColonyId!, current.Identity.SnapshotId!, snapshot,
            f.Ledger.Find(source.EventId)!, payload, f.Ledger, DateTimeOffset.UnixEpoch);
        Check(!Confirm("intruder", "SupportPawnArrived").Result.Accepted, "Support confirmation accepted a different recipient.");
        Check(!Confirm(source.Target.UserId, returning ? "SupportPawnArrived" : "SupportPawnReturnCaravanCreated").Result.Accepted,
            "Support confirmation confused arrival with return.");
        string result = returning ? "SupportPawnReturnCaravanCreated" : "SupportPawnArrived";
        var first = Confirm(source.Target.UserId, result);
        var replay = Confirm(source.Target.UserId, result);
        Check(first.Result.Accepted && first.AppliedSnapshotId == "confirmed" && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && f.Ledger.Find(source.EventId)!.Status == ServerEventStatus.AppliedToSnapshot,
            "Independent support confirmation lost application or its replay receipt.");
    }

    private static void DeletedActors(Func<string, SaveSnapshotPackage> factory)
    {
        var f = Fixture(factory); var created = Send(f); var source = f.Ledger.Find(created.EventId!)!;
        f.Ledger.ChangeStatus(source.EventId, ServerEventStatus.Failed);
        var controls = new[]
        {
            source with { EventId = "applied", IdempotencyKey = "applied", Status = ServerEventStatus.Failed, AppliedSnapshotId = "accepted" },
            source with { EventId = "active", IdempotencyKey = "active" },
            source with { EventId = "rebuilt", IdempotencyKey = "rebuilt", Actor = source.Actor with { ColonyId = "new-colony" }, Status = ServerEventStatus.Failed },
            source with { EventId = "invisible", IdempotencyKey = "invisible", Target = new("other", "away"), Status = ServerEventStatus.Failed }
        };
        foreach (var control in controls) f.Ledger.Append(control);
        var players = new PlayerRegistry(); players.MarkDeleted("userA", "colonyA", "paid", DateTimeOffset.UnixEpoch);
        var reconciliation = new SupportDeliveryReconciliation(f.Ledger, players, f.Notifications);
        long before = f.Notifications.GetVersion("userB");
        lock (f.Gate)
        {
            Check(reconciliation.ReconcileDeletedActors("userB") == 1 && reconciliation.ReconcileDeletedActors("userB") == 0,
                "Deleted support sender reconciliation was not scoped or idempotent.");
        }
        Check(f.Ledger.Find(source.EventId)!.Status == ServerEventStatus.Cancelled && f.Notifications.GetVersion("userB") == before + 1
            && controls.All(item => JsonSerializer.Serialize(item) == JsonSerializer.Serialize(f.Ledger.Find(item.EventId))),
            "Support deletion cleanup changed active, applied, rebuilt or invisible work.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
