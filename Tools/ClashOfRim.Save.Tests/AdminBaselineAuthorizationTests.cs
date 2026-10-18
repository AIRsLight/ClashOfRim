using System.Collections;
using System.Net;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Fixture = HttpSnapshotRecoveryTests.Fixture;

internal static class AdminBaselineAuthorizationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (bool submit in new[] { false, true })
        {
            foreach (string invalid in new[] { "missing-token", "token", "user", "colony", "missing-colony", "protocol", "empty-world", "nonadmin", "logout", "replace", "expired" })
            {
                using var f = new Fixture(factory); f.Start(); f.Seed();
                if (invalid != "empty-world") f.State.WorldConfiguration.Prepare(invalid == "nonadmin" ? "other-admin" : "receiver");
                if (invalid is "logout" or "replace") f.State.LoginSessions.EndUser("receiver");
                if (invalid == "replace") f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _);
                if (invalid == "expired")
                {
                    var old = DateTimeOffset.UtcNow.AddHours(-1);
                    f.State.LoginSessions.TryCreate("receiver", "away", old, out var session);
                    f.Token = f.State.AuthTokens.Issue("receiver", "receiver", "away", session, DateTimeOffset.UtcNow);
                }
                string before = f.Evidence(); var administrators = f.State.WorldConfiguration.ListAdministrators().ToArray();
                using var response = Call(f, submit, invalid);
                Require(!Accepted(response), "Invalid baseline request accepted: " + invalid);
                using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Require(!json.RootElement.GetProperty("isAdministrator").GetBoolean(), "Rejected request claimed administrator privileges.");
                Require(administrators.SequenceEqual(f.State.WorldConfiguration.ListAdministrators()), "Rejected baseline request assigned an administrator.");
                Require(f.State.AdminBaseline.Current is null, "Rejected request published a baseline.");
                f.AssertPending(before, "baseline/" + invalid);
            }
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "role", "failure", "revoke-failure", "compete" })
            {
                using var f = new Fixture(factory); f.Start(); f.State.WorldConfiguration.Prepare("receiver"); f.Seed();
                string? before = null; HttpResponseMessage? response = null;
                SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
                    () => response = Call(f, submit), () =>
                    {
                        if (change is "logout" or "replace") f.State.LoginSessions.EndUser("receiver");
                        if (change == "replace") f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _);
                        if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("receiver");
                        if (change == "role") RevokeRole(f);
                        if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                        if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
                        before = f.Evidence();
                    });
                using (response)
                {
                    bool success = change is "none" or "compete";
                    if (change == "failure") Require(response!.StatusCode == HttpStatusCode.InternalServerError, "Recovery preparation failure was swallowed.");
                    else Require(Accepted(response!) == success, "Wrong baseline recovery outcome: " + change);
                    Require((f.State.AdminBaseline.Current is not null) == (submit && success), "Denied baseline changed prices.");
                    if (success) f.AssertRecovered();
                    else
                    {
                        f.AssertPending(before!, "baseline/recovery/" + change);
                        f.Store.FailPreparation = false; f.RenewToken(); f.State.WorldConfiguration.PromoteAdministrator("receiver");
                        using var retry = Call(f, submit); Require(Accepted(retry), "Fresh admin session failed to resume."); f.AssertRecovered();
                    }
                }
            }
            foreach (bool role in new[] { false, true })
            {
                Action? changed = null;
                var plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.baseline-auth", "test", "1", "test", "", [])
                    { SnapshotPostUploadProcessors = [new Publication(() => changed?.Invoke())] }]);
                using var f = new Fixture(factory, plugins); f.Start(); f.State.WorldConfiguration.Prepare("receiver"); f.Seed();
                changed = () => { if (role) RevokeRole(f); else f.State.AuthTokens.RevokeForUser("receiver"); };
                using var response = Call(f, submit);
                Require(!Accepted(response) && f.State.AdminBaseline.Current is null, "Recovery publication bypassed admin reauthorization.");
                f.AssertRecovered();
            }
        }
        foreach (bool revoke in new[] { false, true }) VerifyNormalization(factory, revoke);
        VerifyPublicationGate();
    }

    private static void VerifyPublicationGate()
    {
        var slot = new RecoveryTestSupport.Slot();
        var state = new ClashOfRimNetworkState(adminBaselineRegistry: new AdminBaselineRegistry(slot));
        slot.Before = _ => Require(Monitor.IsEntered(state.RaidSettlementSnapshotMutationGate), "Baseline persisted outside the shared trade mutation gate.");
        state.WorldConfiguration.Prepare("admin"); var now = DateTimeOffset.UtcNow;
        state.LoginSessions.TryCreate("admin", "colony", now, out var session);
        string token = state.AuthTokens.Issue("admin", "admin", "colony", session, now);
        var response = state.AdministratorBaseline.Submit(
            new SubmitAdminBaselineRequest(ProtocolApiVersion.Current, "admin", "colony", now, [new("Steel", 12)], [], authToken: token));
        Require(response.Result.Accepted && slot.Writes == 1 && state.AdminBaseline.Current!.StandardMarketValuePerThing["Steel"] == 12,
            "Authenticated baseline was not persisted and published.");
    }

    private static void VerifyNormalization(Func<string, SaveSnapshotPackage> factory, bool revoke)
    {
        using var f = new Fixture(factory); f.Start(); f.State.WorldConfiguration.Prepare("receiver");
        var values = new ProbedValues(); SubmitAdminBaselineResponse? result = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => values.BeforeRead = hook,
            () => result = f.State.AdministratorBaseline.Submit(
                new SubmitAdminBaselineRequest(ProtocolApiVersion.Current, "receiver", "away", DateTimeOffset.UtcNow, values, [], authToken: f.Token)),
            () => { if (revoke) f.State.AuthTokens.RevokeForUser("receiver"); });
        Require(result!.Result.Accepted == !revoke && (f.State.AdminBaseline.Current is not null) == !revoke,
            "Normalization did not recheck administrator credentials before publication.");
        if (!revoke) Require(f.State.AdminBaseline.Current!.StandardMarketValuePerThing["Steel"] == 12, "Normalized prices were lost.");
    }

    private static void RevokeRole(Fixture f)
    { f.State.WorldConfiguration.PromoteAdministrator("other-admin"); Require(f.State.WorldConfiguration.RevokeAdministrator("receiver"), "Could not revoke test administrator."); }
    private static HttpResponseMessage Call(Fixture f, bool submit, string invalid = "")
    {
        string protocol = invalid == "protocol" ? "old" : ProtocolApiVersion.Current;
        string user = invalid == "user" ? "impostor" : "receiver";
        string colony = invalid == "colony" ? "elsewhere" : invalid == "missing-colony" ? "" : "away";
        string? token = invalid == "missing-token" ? null : invalid == "token" ? "invalid" : f.Token;
        return submit ? f.Post(ProtocolMessageKind.SubmitAdminBaseline, new SubmitAdminBaselineRequest(protocol, user, colony,
            DateTimeOffset.UtcNow, [new("Steel", 12)], [], authToken: token))
            : f.Post(ProtocolMessageKind.GetAdminBaselineRequirements, new GetAdminBaselineRequirementsRequest(protocol, user, colony, token));
    }
    private static bool Accepted(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return json.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean();
    }
    private sealed class ProbedValues : IReadOnlyList<StandardMarketValueDto>
    {
        public Action? BeforeRead; public int Count => 1; public StandardMarketValueDto this[int index] => new("Steel", 12);
        public IEnumerator<StandardMarketValueDto> GetEnumerator() { BeforeRead?.Invoke(); yield return this[0]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Publication(Action callback) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.baseline-auth";
        public int Order => 350;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) { if (context.UserId == "foreign") callback(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
