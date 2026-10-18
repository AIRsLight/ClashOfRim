using System.Net.Http.Json;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using HttpFixture = HttpSnapshotRecoveryTests.Fixture;

internal static class DiplomacyAuthorizationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        var failures = new List<string>();
        foreach (bool create in new[] { true, false })
        foreach (string change in new[] { "missing-token", "invalid-token", "foreign-token", "wrong-user", "wrong-colony",
            "logout", "replace", "revoke", "expired", "stale", "empty", "missing-snapshot" })
        {
            using var f = new Fixture(factory); f.Http.Seed();
            string user = change == "wrong-user" ? "intruder" : "receiver";
            string colony = change == "wrong-colony" ? "retired" : "away";
            string snapshot = change == "stale" ? "old" : change == "empty" ? "" : "base";
            string? token = change switch
            {
                "missing-token" => null, "invalid-token" => "invalid", "foreign-token" => f.Token("intruder", "elsewhere"),
                "expired" => f.Http.State.AuthTokens.Issue("receiver", "receiver", "away", "expired", DateTimeOffset.UtcNow.AddDays(-1)),
                _ => f.Http.Token
            };
            if (change == "missing-snapshot") f.Http.Store.RemoveLatest("receiver", "away");
            if (change is "logout" or "replace") f.Http.State.LoginSessions.EndUser("receiver");
            if (change == "replace") f.Token("receiver", "away");
            if (change == "revoke") f.Http.State.AuthTokens.RevokeForUser("receiver");
            string before = f.Evidence();
            var result = f.Call(create, token, user, colony, snapshot);
            var expected = change == "empty" ? ProtocolErrorCode.ValidationFailed
                : change is "stale" or "missing-snapshot" ? ProtocolErrorCode.SnapshotMismatch
                : change is "invalid-token" or "expired" or "logout" or "replace" or "revoke" ? ProtocolErrorCode.SessionUnavailable : ProtocolErrorCode.Unauthorized;
            if (result.Result.Accepted || result.Result.ErrorCode != expected || before != f.Evidence() || !f.Http.State.SnapshotCommits.HasPending)
                failures.Add($"{(create ? "create" : "respond")}/{change}: {result.Result.ErrorCode}, accepted={result.Result.Accepted}, unchanged={before == f.Evidence()}, pending={f.Http.State.SnapshotCommits.HasPending}");
        }
        Require(failures.Count == 0, "Diplomacy authorization/recovery violations:\n" + string.Join("\n", failures));

        foreach (bool create in new[] { true, false })
        foreach (string conflict in create ? new[] { "actor", "target", "type", "kind" } : new[] { "target", "type", "missing" })
        {
            using var f = new Fixture(factory); f.Http.Seed();
            var source = AuthoritativeEventFactory.Create(ServerEventType.WarDeclaration,
                new("receiver", "away"), new("sender", "home"), "outgoing", false,
                new WarDeclarationEventPayload("outgoing", "war"), DateTimeOffset.UtcNow);
            if (!create) source = source with { Actor = new("sender", "home"), Target = new("receiver", "away") };
            source = conflict switch
            {
                "actor" => source with { Actor = new("other", "elsewhere") },
                "target" => source with { Target = new("other", "elsewhere") },
                "type" => source with { Type = f.Http.Source.Type, Payload = f.Http.Source.Payload },
                _ => source
            };
            if (create || conflict != "missing") f.Http.State.Ledger.Append(source);
            string before = f.Evidence();
            var result = f.Call(create, f.Http.Token, eventId: conflict == "missing" ? "unknown" : source.EventId,
                kind: conflict == "kind" ? "AllianceRequest" : "WarDeclaration");
            Require(!result.Result.Accepted && result.Result.ErrorCode == (create ? ProtocolErrorCode.Conflict : ProtocolErrorCode.EventNotFound)
                && result.NotificationEventId is null && result.RelationKind is null && before == f.Evidence() && f.Http.State.SnapshotCommits.HasPending,
                "Unrelated diplomacy key/event triggered recovery or disclosed a receipt: " + create + "/" + conflict);
        }

        foreach (bool create in new[] { true, false })
        foreach (string change in new[] { "none", "logout", "replace", "revoke", "snapshot", "failure", "revoke-failure" })
        {
            using var f = new Fixture(factory); f.Http.Seed();
            DiplomacyEventResponse? result = null; string before = f.Evidence();
            SnapshotPreparationGateProbe.Run(f.Http.State.RaidSettlementSnapshotMutationGate, hook => f.Http.Store.BeforePrepare = hook,
                () => result = f.Call(create, f.Http.Token, expectPreparationFailure: change == "failure"), () =>
                {
                    if (change is "logout" or "replace") f.Http.State.LoginSessions.EndUser("receiver");
                    if (change == "replace") f.Token("receiver", "away");
                    if (change is "revoke" or "revoke-failure") f.Http.State.AuthTokens.RevokeForUser("receiver");
                    if (change == "snapshot") f.Snapshot("advanced");
                    if (change is "failure" or "revoke-failure") f.Http.Store.FailPreparation = true;
                    before = f.Evidence();
                });
            if (change == "none")
            {
                Require(result!.Result.Accepted, "Authorized diplomacy did not finish recovery."); f.Http.AssertRecovered();
            }
            else
            {
                Require(!result!.Result.Accepted && before == f.Evidence() && f.Http.State.SnapshotCommits.HasPending,
                    "Diplomacy used revoked identity/baseline or failed preparation: " + change);
                if (change != "failure") Require(result.Result.ErrorCode == (change == "snapshot" ? ProtocolErrorCode.SnapshotMismatch : ProtocolErrorCode.SessionUnavailable), "Wrong recovery revalidation result.");
                f.Http.Store.FailPreparation = false; f.Http.RenewToken(); f.Snapshot("base");
                Require(f.Call(create, f.Http.Token).Result.Accepted, "Valid retry could not recover pending diplomacy."); f.Http.AssertRecovered();
            }
        }

        foreach (bool create in new[] { true, false })
        foreach (bool accept in new[] { true, false })
        {
            using var f = new Fixture(factory);
            var first = f.Call(create, f.Http.Token, accepted: accept);
            Require(first.Result.Accepted, "Valid diplomacy command failed.");
            f.Snapshot("advanced"); string before = f.Evidence();
            var duplicate = f.Call(create, f.Http.Token, accepted: !accept);
            Require(duplicate.Result is { Accepted: true, ErrorCode: ProtocolErrorCode.DuplicateRequest }
                && duplicate.EventId == first.EventId && f.Evidence() == before, "Diplomacy replay lost the original decision after snapshot advancement.");
            Require(duplicate.RelationKind == f.Http.State.DiplomacyRelations.GetRelationKind("sender", "home", "receiver", "away"), "Diplomacy replay did not return the authoritative relation.");
            foreach (var token in new string?[] { null, "invalid", f.Token("foreign", "foreign-colony") })
            {
                var denied = f.Call(create, token);
                Require(!denied.Result.Accepted && denied.Result.ErrorCode == (token == "invalid" ? ProtocolErrorCode.SessionUnavailable : ProtocolErrorCode.Unauthorized) && f.Evidence() == before, "Cached diplomacy receipt bypassed authentication.");
            }
            if (create)
            {
                var conflict = f.Call(true, f.Http.Token, target: "another");
                Require(!conflict.Result.Accepted && conflict.Result.ErrorCode == ProtocolErrorCode.Conflict && conflict.EventId is null
                    && f.Evidence() == before, "Diplomacy key reuse exposed an unrelated receipt.");
            }
        }

        using (var f = new Fixture(factory))
        {
            var replies = Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => f.Call(false, f.Http.Token, accepted: i % 2 == 0)))).GetAwaiter().GetResult();
            Require(replies.All(r => r.Result.Accepted) && replies.Count(r => r.Result.ErrorCode != ProtocolErrorCode.DuplicateRequest) == 1,
                "Concurrent decisions both executed.");
            Require(f.Http.State.Ledger.ListAll().Count(e => e.Payload is ServerNotificationEventPayload { RelatedEventId: "diplomacy" }) == 1,
                "Concurrent decisions duplicated response notifications.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly HttpFixture Http;
        private readonly Func<string, SaveSnapshotPackage> factory;
        internal Fixture(Func<string, SaveSnapshotPackage> factory)
        {
            this.factory = factory; Http = new(factory); Snapshot("base"); Http.Start();
            Http.State.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.AllianceRequest,
                new("sender", "home"), new("receiver", "away"), "incoming", false,
                new AllianceRequestEventPayload("incoming", "join", null), DateTimeOffset.UtcNow) with { EventId = "diplomacy" });
        }
        internal void Snapshot(string id)
        {
            var p = factory(id); var envelope = p.Envelope with { Identity = new("receiver", "away", id) };
            Http.Store.StoreLatest(new LatestSnapshotRecord(envelope.Identity, envelope, p.Index, DateTimeOffset.UtcNow));
        }
        internal string Token(string user, string colony)
        {
            Http.State.LoginSessions.TryCreate(user, colony, DateTimeOffset.UtcNow, out var session);
            return Http.State.AuthTokens.Issue(user, user, colony, session, DateTimeOffset.UtcNow);
        }
        internal DiplomacyEventResponse Call(bool create, string? token, string user = "receiver", string colony = "away", string snapshot = "base", bool accepted = true, string target = "sender",
            string eventId = "diplomacy", string kind = "WarDeclaration", bool expectPreparationFailure = false)
        {
            object request = create
                ? new { idempotencyKey = "outgoing", actor = new ProtocolIdentity(user, colony, snapshot), target = new ProtocolIdentity(target, "home", null), kind, message = "war", expiresAtUtc = (DateTimeOffset?)null, authToken = token }
                : new { eventId, userId = user, colonyId = colony, currentSnapshotId = snapshot, accepted, reason = "decision", authToken = token };
            using var response = Http.Post(create ? ProtocolMessageKind.CreateDiplomacyEvent : ProtocolMessageKind.RespondDiplomacyEvent, request);
            if (expectPreparationFailure)
            {
                Require(response.StatusCode == System.Net.HttpStatusCode.InternalServerError, "Injected preparation failure did not reach the HTTP boundary.");
                return new(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, "HTTP preparation failure"), null, null, null);
            }
            response.EnsureSuccessStatusCode();
            return response.Content.ReadFromJsonAsync<DiplomacyEventResponse>().GetAwaiter().GetResult()!;
        }
        internal string Evidence() => Http.Evidence() + JsonSerializer.Serialize(Http.State.DiplomacyRelations.GetRelation("sender", "home", "receiver", "away"));
        public void Dispose() => Http.Dispose();
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
