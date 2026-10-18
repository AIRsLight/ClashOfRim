using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class DiplomacyModuleTests
{
    private static readonly string[] Kinds = ["AllianceRequest", "AllianceCancellation", "WarDeclaration", "PeaceRequest", "SupportRequest"];
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clash-diplomacy-module-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string backend in new[] { "memory", "file", "sqlite" })
                foreach (string kind in Kinds)
                foreach (bool online in new[] { false, true })
                {
                    var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
                    var memory = new InMemoryAuthoritativeEventLedger(); var relations = new DiplomacyRelationRegistry();
                    IAuthoritativeEventLedger Ledger() => backend switch
                    {
                        "file" => new FileAuthoritativeEventLedger(Path.Combine(path, "events.json")),
                        "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(path, "events.sqlite")), _ => memory
                    };
                    DiplomacyRelationRegistry Relations() => backend switch
                    {
                        "file" => new(Path.Combine(path, "relations.json")),
                        "sqlite" => new(new SqliteDomainKeyedJsonRecordStore(Path.Combine(path, "events.sqlite"), SqliteDomainRegistrySchema.DiplomacyRelations), null),
                        _ => relations
                    };
                    Lifecycle(factory, kind, online, Ledger, Relations);
                }
                TransitionMatrix(factory);
                CooldownBoundaries();
            }
            ConfigurationAndRecovery(factory);
            Rejections(factory);
            ConcurrentDecisions(factory);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture; SqliteConnection.ClearAllPools();
            if (!string.Equals(Directory.GetParent(root)!.FullName, Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary test path escaped its parent.");
            Directory.Delete(root, recursive: true);
        }
    }
    private static EconomicModuleTestContext Fixture(Func<string, SaveSnapshotPackage> factory,
        IAuthoritativeEventLedger? ledger = null, DiplomacyRelationRegistry? relations = null)
    {
        var f = new EconomicModuleTestContext(factory, ledger: ledger, relations: relations)
            { Configuration = new(diplomacyRelationChangeCooldown: TimeSpan.Zero, diplomacySupportRequestCooldown: TimeSpan.Zero) };
        var identity = new SnapshotIdentity("userB", "colonyB", "target");
        f.Snapshots.StoreLatest(new(identity, f.Package.Envelope with { Identity = identity }, f.Package.Index, DateTimeOffset.UtcNow));
        return f;
    }
    private static CreateDiplomacyEventRequest Request(EconomicModuleTestContext f, string kind, string key = "request", string? token = null)
        => new(key, new("userA", "colonyA", "before"), new("userB", "colonyB", "target"), kind, "盟约-é-İ", DateTimeOffset.UnixEpoch.AddDays(100), token ?? f.Token);
    private static RespondDiplomacyEventRequest Response(string id, string token, bool accepted = true)
        => new(id, "userB", "colonyB", "target", accepted, "decision-é-İ", token);
    private static string Initial(string kind) => kind switch
    { "AllianceCancellation" or "SupportRequest" => "Ally", "PeaceRequest" => "Hostile", _ => "Neutral" };
    private static string Final(string kind) => kind switch
    { "AllianceRequest" or "SupportRequest" => "Ally", "WarDeclaration" => "Hostile", _ => "Neutral" };
    private static void SetRelation(EconomicModuleTestContext f, string relation, DateTimeOffset? now = null)
        => f.Relations.SetRelationKind("userA", "colonyA", "userB", "colonyB", relation, "initial", now ?? DateTimeOffset.UtcNow.AddDays(-10));
    private static string Relation(EconomicModuleTestContext f) => f.Relations.GetRelationKind("userA", "colonyA", "userB", "colonyB");
    private static string Evidence(EconomicModuleTestContext f) => JsonSerializer.Serialize(f.Ledger.ListAll()) + JsonSerializer.Serialize(f.Relations.List());

    private static void Lifecycle(Func<string, SaveSnapshotPackage> factory, string kind, bool online,
        Func<IAuthoritativeEventLedger> ledger, Func<DiplomacyRelationRegistry> relations)
    {
        var f = Fixture(factory, ledger(), relations()); SetRelation(f, Initial(kind));
        OnlinePresenceLease? receiver = null, sender = null;
        if (online) { f.Presence.TryConnectExclusive("userB", out receiver); f.Presence.TryConnectExclusive("userA", out sender); }
        using (receiver) using (sender)
        {
            // Case-insensitive protocol names must not depend on Turkish casing.
            var first = f.Diplomacy.Create(Request(f, kind.ToLowerInvariant()));
            Check(first.Result.Accepted && first.EventId is not null && first.NotificationEventId is null, "Valid create failed: " + kind);
            var source = f.Ledger.Find(first.EventId!)!;
            Check(source.Type == DiplomacyPolicy.ResolveDiplomacyEventType(kind) && source.Actor == new EventParty("userA", "colonyA")
                && source.Target == new EventParty("userB", "colonyB"), "Create changed captured endpoints.");
            Check(source.Status == (online ? ServerEventStatus.ReadyForImmediateDelivery : ServerEventStatus.PendingOfflineDelivery)
                && f.Notifications.GetVersion("userB") == 1, "Delivery or wake-up changed: " + kind);
            if (kind == "SupportRequest")
            {
                var notice = (ServerNotificationEventPayload)source.Payload;
                Check(notice.NotificationId == "support-request:request" && notice.Message.Contains("盟约-é-İ") && !notice.OnlineOnly
                    && notice.RelatedEventId is null && !notice.FromAdministrator && first.RelationKind == "Ally", "Support request lost its persisted notice contract.");
            }
            else
            {
                string token = f.Issue("userB", "colonyB");
                var result = f.Diplomacy.Respond(Response(first.EventId!, token));
                Check(result.Result.Accepted && result.NotificationEventId is not null && Relation(f) == Final(kind), "Decision did not apply relation: " + kind);
                var changed = f.Ledger.Find(first.EventId!)!;
                var notification = f.Ledger.Find(result.NotificationEventId!)!;
                Check(changed.Status == ServerEventStatus.AppliedToSnapshot && changed.TargetDecision == TargetEventDecision.Accepted
                    && changed.DecisionReason == "decision-é-İ" && notification.Actor.UserId == "server" && notification.Target == source.Actor,
                    "Decision/response lost original identity.");
                var payload = (ServerNotificationEventPayload)notification.Payload;
                Check(payload.NotificationId == $"diplomacy-response:{first.EventId}:True" && payload.RelatedEventId == first.EventId
                    && payload.RelatedEventType == source.Type && payload.RelatedUserId == "userB" && payload.RelatedColonyId == "colonyB"
                    && payload.RelatedAccepted == true && f.Notifications.GetVersion("userA") == 1, "Response association changed.");
                string before = Evidence(f);
                var reversed = f.Diplomacy.Respond(Response(first.EventId!, token, false));
                Check(reversed.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && before == Evidence(f), "Duplicate reversed the persisted decision.");
            }
            string evidence = Evidence(f);
            // Reopening independently persisted ledger + relations preserves the existing result, even with new policy/base.
            var reopened = Fixture(factory, ledger(), relations());
            reopened.Configuration = new(diplomacyRelationChangeCooldown: TimeSpan.FromDays(100), diplomacySupportRequestCooldown: TimeSpan.FromDays(100));
            var advanced = reopened.Snapshots.GetLatest("userA", "colonyA")!;
            reopened.Snapshots.StoreLatest(advanced with { Identity = advanced.Identity with { SnapshotId = "advanced" } });
            var replay = reopened.Diplomacy.Create(Request(reopened, kind));
            Check(replay.Result is { Accepted: true, ErrorCode: ProtocolErrorCode.DuplicateRequest } && replay.EventId == first.EventId
                && replay.RelationKind == Final(kind) && Evidence(reopened) == evidence, "Persisted replay changed evidence or obeyed new cooldown.");
        }
    }
    private static void TransitionMatrix(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string relation in new[] { "Neutral", "Ally", "Hostile" })
        foreach (string kind in Kinds)
        {
            var f = Fixture(factory); SetRelation(f, relation);
            var result = f.Diplomacy.Create(Request(f, kind));
            Check(result.Result.Accepted == (relation == Initial(kind)), "Invalid relation transition: " + kind + "/" + relation);
            if (!result.Result.Accepted) Check(f.Ledger.ListAll().Count == 0 && Relation(f) == relation, "Denied transition mutated state.");
        }
    }
    private static void CooldownBoundaries()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(20);
        var relation = new DiplomacyRelationRecord("a", "c", "b", "d", "Neutral", "event", now);
        foreach (long ticks in new[] { -1L, 0L, 1L })
        {
            var error = DiplomacyPolicy.ValidateDiplomacyRelationTransition(ServerEventType.AllianceRequest, "Neutral", relation, now.AddHours(1).AddTicks(ticks), TimeSpan.FromHours(1));
            Check((error is null) == (ticks >= 0), "Cooldown equality boundary changed.");
            Check((DiplomacyPolicy.ValidateSupportRequestCooldown(now, now.AddMinutes(1).AddTicks(ticks), TimeSpan.FromMinutes(1)) is null) == (ticks >= 0),
                "Support cooldown equality boundary changed.");
        }
        Check(DiplomacyPolicy.ValidateDiplomacyRelationCooldown(relation, now, TimeSpan.Zero) is null
            && DiplomacyPolicy.ValidateDiplomacyRelationCooldown(null, now, TimeSpan.MaxValue) is null, "Disabled/missing cooldown rejected.");
    }
    private static void ConfigurationAndRecovery(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string kind in new[] { "AllianceRequest", "SupportRequest" })
        foreach (string change in new[] { "config", "relation", "snapshot", "logout", "revoke" })
        foreach (bool preparing in new[] { false, true })
        {
            var f = Fixture(factory); SetRelation(f, Initial(kind), DateTimeOffset.UtcNow);
            if (kind == "SupportRequest") Check(f.Diplomacy.Create(Request(f, kind, "first")).Result.Accepted, "Initial support request failed.");
            f.Recovery.NeedsPreparation = preparing;
            void Change()
            {
                Check(Monitor.IsEntered(f.Gate) != preparing, "Recovery preparation held mutation gate or replay lost it.");
                switch (change)
                {
                    case "config": f.Configuration = new(diplomacyRelationChangeCooldown: TimeSpan.FromDays(1), diplomacySupportRequestCooldown: TimeSpan.FromDays(1)); break;
                    case "relation": SetRelation(f, "Hostile"); break;
                    case "snapshot": var latest = f.Snapshots.GetLatest("userA", "colonyA")!; f.Snapshots.StoreLatest(latest with { Identity = latest.Identity with { SnapshotId = "new" } }); break;
                    case "logout": f.Sessions.EndUser("userA"); break;
                    case "revoke": f.Tokens.RevokeForUser("userA"); break;
                }
            }
            if (preparing) f.Recovery.OnPrepare = Change; else f.Recovery.OnReplay = Change;
            int before = f.Ledger.ListAll().Count;
            Check(!f.Diplomacy.Create(Request(f, kind)).Result.Accepted && f.Ledger.ListAll().Count == before, "Recovered change was ignored: " + kind + "/" + change);
        }
        var support = Fixture(factory); SetRelation(support, "Ally");
        Check(support.Diplomacy.Create(Request(support, "SupportRequest", "first")).Result.Accepted, "Support seed failed.");
        support.Configuration = new(diplomacySupportRequestCooldown: TimeSpan.FromDays(1));
        Check(!support.Diplomacy.Create(Request(support, "SupportRequest", "second")).Result.Accepted, "Support cooldown ignored.");
        support.Configuration = new(diplomacySupportRequestCooldown: TimeSpan.Zero);
        Check(support.Diplomacy.Create(Request(support, "SupportRequest", "second")).Result.Accepted, "Disabled support cooldown remained cached.");
    }
    private static void Rejections(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string kind in new[] { "AllianceRequest", "PeaceRequest", "AllianceCancellation", "WarDeclaration" })
        {
            var f = Fixture(factory); SetRelation(f, Initial(kind)); var source = f.Diplomacy.Create(Request(f, kind));
            var response = f.Diplomacy.Respond(Response(source.EventId!, f.Issue("userB", "colonyB"), false));
            Check(response.Result.Accepted == (kind is "AllianceRequest" or "PeaceRequest"), "Forced diplomatic notice became rejectable.");
            if (response.Result.Accepted) Check(f.Ledger.Find(source.EventId!)!.TargetDecision == TargetEventDecision.Rejected && Relation(f) == Initial(kind), "Rejected invitation applied relation.");
        }
        foreach (string kind in new[] { "", "unknown", " AllianceRequest", "SERVERNOTIFICATION" })
        {
            var f = Fixture(factory); var response = f.Diplomacy.Create(Request(f, kind));
            Check(response.Result.ErrorCode == ProtocolErrorCode.ValidationFailed && f.Recovery.Replays == 0 && f.Ledger.ListAll().Count == 0, "Invalid kind entered recovery.");
        }
        var invalid = Fixture(factory);
        Check(invalid.Diplomacy.Create(Request(invalid, "AllianceRequest", token: "invalid")).Result.ErrorCode == ProtocolErrorCode.Unauthorized
            && invalid.Recovery.Replays == 0, "Invalid token started recovery.");
    }
    private static void ConcurrentDecisions(Func<string, SaveSnapshotPackage> factory)
    {
        var f = Fixture(factory); var created = f.Diplomacy.Create(Request(f, "AllianceRequest")); var token = f.Issue("userB", "colonyB");
        var responses = Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => f.Diplomacy.Respond(Response(created.EventId!, token, i % 2 == 0))))).GetAwaiter().GetResult();
        Check(responses.All(r => r.Result.Accepted) && responses.Count(r => r.Result.ErrorCode != ProtocolErrorCode.DuplicateRequest) == 1
            && f.Ledger.ListAll().Count == 2 && f.Notifications.GetVersion("userA") == 1, "Independent module allowed competing decisions.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
