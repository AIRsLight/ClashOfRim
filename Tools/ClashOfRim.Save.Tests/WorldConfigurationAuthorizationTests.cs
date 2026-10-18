using System.Net;
using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Fixture = HttpSnapshotRecoveryTests.Fixture;

internal static class WorldConfigurationAuthorizationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string invalid in new[] { "missing-token", "token", "user", "colony", "missing-user", "missing-colony", "protocol",
            "missing-world", "seed", "coverage", "coverage-nan", "coverage-infinity", "coverage-overflow", "factions", "empty-world", "nonadmin", "logout", "replace", "expired" })
        {
            using var e = new Env(factory, invalid == "empty-world" ? null : invalid == "nonadmin" ? "other-admin" : "receiver");
            var f = e.F; f.Seed();
            Change(e, invalid);
            if (invalid == "expired")
            {
                f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow.AddHours(-1), out var session);
                f.Token = f.State.AuthTokens.Issue("receiver", "receiver", "away", session, DateTimeOffset.UtcNow);
            }
            string before = f.Evidence(), world = e.Evidence();
            using var response = Call(e, invalid); Require(!Accepted(response), "Invalid world submission accepted: " + invalid);
            Require(e.Provider.Normalizations == (invalid is "seed" or "coverage" or "coverage-nan" or "coverage-infinity" or "coverage-overflow" or "factions" ? 1 : 0),
                "Unauthorized or malformed request invoked extension normalization: " + invalid);
            using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Require(!json.RootElement.GetProperty("isAdministrator").GetBoolean()
                && json.RootElement.GetProperty("worldConfiguration").ValueKind == JsonValueKind.Null, "Denied request disclosed world/administrator state.");
            Require(world == e.Evidence(), "Rejected world request changed configuration, administrators, storage or notifications: " + invalid);
            f.AssertPending(before, "world/" + invalid);
        }
        foreach (string stage in new[] { "normalize", "recovery" })
        foreach (string change in stage == "normalize"
            ? new[] { "none", "logout", "replace", "revoke", "role", "compatibility" }
            : new[] { "none", "logout", "replace", "revoke", "role", "compatibility", "failure", "revoke-failure", "compete" })
        {
            using var e = new Env(factory); var f = e.F; f.Seed();
            string? before = null, world = null; HttpResponseMessage? response = null;
            SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate,
                hook => { if (stage == "normalize") e.Provider.BeforeNormalize = hook; else f.Store.BeforePrepare = hook; },
                () => response = Call(e), () =>
                {
                    Change(e, change);
                    if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                    if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
                    before = f.Evidence(); world = e.Evidence();
                });
            using (response)
            {
                bool success = change is "none" or "compete";
                if (change == "failure") Require(response!.StatusCode == HttpStatusCode.InternalServerError, "World recovery preparation failure was swallowed.");
                else Require(Accepted(response!) == success, "Wrong world authorization outcome: " + stage + "/" + change);
                if (!success)
                {
                    Require(world == e.Evidence(), "Rejected prepared world submission changed authoritative world state.");
                    f.AssertPending(before!, "world/" + stage + "/" + change);
                    f.Store.FailPreparation = false; f.RenewToken(); f.State.WorldConfiguration.PromoteAdministrator("receiver");
                    using var retry = Call(e); Require(Accepted(retry), "Fresh world request did not resume after rejection.");
                }
                e.AssertSubmitted(); f.AssertRecovered();
            }
        }
        foreach (string change in new[] { "revoke", "role", "compatibility" })
        {
            using var e = new Env(factory); var f = e.F; f.Seed();
            string? world = null;
            e.Published = () => { Change(e, change); world = e.Evidence(); };
            using var response = Call(e);
            Require(!Accepted(response) && world == e.Evidence(), "Recovery publication bypassed final world authorization."); f.AssertRecovered();
            e.Published = null; f.RenewToken(); f.State.WorldConfiguration.PromoteAdministrator("receiver");
            using var retry = Call(e); Require(Accepted(retry), "Fresh session did not resume after recovery publication."); e.AssertSubmitted();
        }
    }

    private static void Change(Env e, string change)
    {
        var f = e.F;
        if (change is "logout" or "replace") f.State.LoginSessions.EndUser("receiver");
        if (change == "replace") f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _);
        if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("receiver");
        if (change == "role")
        { f.State.WorldConfiguration.PromoteAdministrator("other-admin"); Require(f.State.WorldConfiguration.RevokeAdministrator("receiver"), "Test role revocation failed."); }
        if (change == "compatibility") f.State.CompatibilityBaseline.EnsureBaseline(new CompatibilityManifest { ManifestId = "changed" }, "receiver", DateTimeOffset.UtcNow);
    }
    private static HttpResponseMessage Call(Env e, string invalid = "")
    {
        e.Slot.RequireGate = true;
        try { return e.F.Post(ProtocolMessageKind.SubmitWorldConfiguration, new SubmitWorldConfigurationRequest(
            invalid == "protocol" ? "old" : ProtocolApiVersion.Current,
            invalid == "user" ? "impostor" : invalid == "missing-user" ? "" : "receiver",
            invalid == "colony" ? "elsewhere" : invalid == "missing-colony" ? "" : "away",
            invalid == "missing-world" ? null! : Configuration("new-seed", invalid),
            invalid == "missing-token" ? null : invalid == "token" ? "invalid" : e.F.Token)); }
        finally { e.Slot.RequireGate = false; }
    }
    private static WorldConfigurationDto Configuration(string seed, string invalid = "") => new("world-test", "spoofed-user", "spoofed-colony",
        DateTimeOffset.UnixEpoch, invalid == "seed" ? "" : seed, invalid switch { "coverage" => "zero", "coverage-nan" => "NaN", "coverage-infinity" => "Infinity", "coverage-overflow" => "1e100", _ => "30" }, "Normal", "Normal", "Normal", "Normal", "100",
        invalid == "factions" ? [] : ["OutlanderCivil"], [], [], [], [], []);
    private static bool Accepted(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return json.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean();
    }
    private sealed class Env : IDisposable
    {
        public readonly Fixture F; public readonly Slot Slot = new(); public readonly Provider Provider = new(); public Action? Published;
        public Env(Func<string, SaveSnapshotPackage> factory, string? administrator = "receiver")
        {
            var plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.world-auth", "test", "1", "test", "", [], WorldConfigurationExtensionProviders: [Provider])
                { SnapshotPostUploadProcessors = [new Publication(() => Published?.Invoke())] }]);
            F = new Fixture(factory, plugins, new WorldConfigurationRegistry(Slot)); Slot.Gate = F.State.RaidSettlementSnapshotMutationGate;
            F.Start();
            if (administrator is not null) { F.State.WorldConfiguration.Prepare(administrator); F.State.WorldConfiguration.Submit(administrator, Configuration("old-seed")); }
        }
        // Pending configuration is intentionally hidden by Current until its substrate arrives.
        private WorldConfigurationDto? Pending => (WorldConfigurationDto?)typeof(WorldConfigurationRegistry)
            .GetField("worldConfiguration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(F.State.WorldConfiguration);
        public string Evidence() => JsonSerializer.Serialize(new { pending = Pending, stored = Slot.Read(),
            admins = F.State.WorldConfiguration.ListAdministrators(), players = F.State.Players.List(),
            version = F.State.WorldConfigurationNotifications.GetVersion("receiver") });
        public void AssertSubmitted() => Require(Pending?.SeedString == "new-seed" && Pending.ConfiguredByUserId == "receiver"
            && Pending.ConfiguredByColonyId == "away" && Slot.Read()!.Contains("new-seed"), "Submitted world lost its values, actor binding or durable record.");
        public void Dispose() => F.Dispose();
    }
    private sealed class Slot : IJsonPersistenceSlot
    {
        public object Gate = null!; public bool RequireGate; private string? value;
        public string? Read() => value;
        public void Write(string json)
        { if (RequireGate) Require(Monitor.IsEntered(Gate), "World publication is outside the shared mutation gate."); value = json; }
    }
    internal sealed class Provider : IWorldConfigurationExtensionProvider
    {
        public Action? BeforeNormalize; public int Normalizations;
        internal Func<WorldConfigurationExtensionSnapshotContext, IReadOnlyList<WorldConfigurationExtensionDto>>? FromSnapshot;
        internal IReadOnlyList<WorldConfigurationExtensionKey> Keys = [];
        public IReadOnlyList<WorldConfigurationExtensionKey> HandledKeys => Keys;
        public IReadOnlyList<WorldConfigurationExtensionDto> NormalizeSubmittedExtensions(WorldConfigurationExtensionContext context, IReadOnlyList<WorldConfigurationExtensionDto> incoming)
        { Normalizations++; BeforeNormalize?.Invoke(); return incoming; }
        public IReadOnlyList<WorldConfigurationExtensionDto> MergeExtensions(WorldConfigurationExtensionContext context, IReadOnlyList<WorldConfigurationExtensionDto> current, IReadOnlyList<WorldConfigurationExtensionDto> incoming) => incoming;
        public IReadOnlyList<WorldConfigurationExtensionDto> RemoveColonyExtensions(string userId, string colonyId, IReadOnlyList<WorldConfigurationExtensionDto> current) => current;
        public IReadOnlyList<WorldConfigurationExtensionDto> BuildDeliveryExtensions(IReadOnlyList<WorldConfigurationExtensionDto> current) => current;
        public IReadOnlyList<WorldConfigurationExtensionDto> BuildExtensionsFromAcceptedSnapshot(WorldConfigurationExtensionSnapshotContext context) => FromSnapshot?.Invoke(context) ?? [];
    }
    private sealed class Publication(Action callback) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.world-auth"; public int Order => 350;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) { if (context.UserId == "foreign") callback(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
