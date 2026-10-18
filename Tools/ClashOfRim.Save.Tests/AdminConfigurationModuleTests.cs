using AIRsLight.ClashOfRim.Network.Modules.Admin;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class AdminConfigurationModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string mode in new[] { "save", "disclosure" }) { Verify(mode); count++; }
                foreach (string phase in new[] { "intent", "compatibility", "audit", "complete" })
                    foreach (bool reopen in new[] { false, true }) { PublicationFailure(new(), phase, reopen); count++; }
                foreach (bool replace in new[] { false, true }) foreach (bool replay in new[] { false, true })
                    foreach (string change in new[] { "none", "role", "credential" }) { RecoveryChanges(replace, replay, change); count++; }
                MappingAndAudit(); count++;
                Status(); count++;
                OverrideOrdering(); count++;
                CorruptPending(); count++;
                RegistryReplacement(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        foreach (string store in new[] { "file", "sqlite" }) foreach (string phase in new[] { "compatibility", "audit", "complete" })
        { PersistentFailure(store, phase); count++; }
        HttpAuthorization(factory); count++;
        HttpFlows(factory); count += 6;
        StartupRecovery(); count++;
        foreach (bool trim in new[] { false, true }) { StructuredAuditFailure(trim); count++; }
        Console.WriteLine($"PASS: administrator configuration module ({count} scenarios)");
    }

    private static void Verify(string mode)
    {
        var slot = new RecoveryTestSupport.Slot();
        var state = new ClashOfRimNetworkState(serverConfiguration: new(pvpEnabled: false), serverConfigurationOverrides: new ServerConfigurationRegistry(slot));
        state.WorldConfiguration.Prepare("admin");
        state.CompatibilityBaseline.ReplaceBaseline(EntryTestSupport.Manifest() with
        { Mods = [new ModManifestEntry { PackageId = "test", Role = ModCompatibilityRole.Required }] }, "admin", DateTimeOffset.UtcNow);
        state.LoginSessions.TryCreate("admin", "colony", DateTimeOffset.UtcNow, out var session);
        string token = state.AuthTokens.Issue("admin", "admin", "colony", session, DateTimeOffset.UtcNow);
        var dto = state.ConfigurationChanges.Current;
        var node = JsonSerializer.SerializeToNode(dto)!;
        node["PvpEnabled"] = true;
        node["CompatibilityMods"]![0]!["Role"] = "Optional";
        dto = node.Deserialize<AdminConfigurationDto>()!;
        if (mode == "disclosure")
        {
            var response = Call(state, new("admin", "colony", "bad", dto));
            Require(!response.Result.Accepted && response.Configuration is null, "Unauthorized update disclosed administrator configuration.");
            return;
        }
        slot.Fail = true;
        try { Call(state, new("admin", "colony", token, dto)); throw new Exception("Save failure was not injected."); }
        catch (IOException) { }
        Require(!state.ServerConfiguration.PvpEnabled && state.ServerConfigurationOverrides.Current is null
            && state.CompatibilityBaseline.Current!.Mods[0].Role == ModCompatibilityRole.Required,
            "Failed configuration persistence published runtime configuration, overrides or compatibility roles.");
    }

    private static void HttpAuthorization(Func<string, SaveSnapshotPackage> factory)
    {
        using var f = new HttpSnapshotRecoveryTests.Fixture(factory); f.Start(); f.Seed();
        string before = f.Evidence();
        using var response = f.Post(ProtocolMessageKind.AdminUpdateConfiguration, new AdminUpdateConfigurationRequest("receiver", "away", "bad", null));
        response.EnsureSuccessStatusCode();
        Require(!response.Content.ReadFromJsonAsync<AdminUpdateConfigurationResponse>().GetAwaiter().GetResult()!.Result.Accepted, "Bad token accepted.");
        f.AssertPending(before, "unauthorized admin configuration");
    }

    private static AdminUpdateConfigurationResponse Call(ClashOfRimNetworkState state, AdminUpdateConfigurationRequest request)
        => state.AdministratorConfiguration.Update(request);

    private static void PublicationFailure(Slots slots, string phase, bool reopen)
    {
        var f = new Env(slots);
        if (phase == "intent") slots.Settings.Fail = true;
        if (phase == "compatibility") slots.Compatibility.Fail = true;
        if (phase == "audit") slots.Audit.Fail = true;
        if (phase == "complete") slots.Settings.Before = json =>
        { if (JsonNode.Parse(json)!["Pending"] is null) throw new IOException("Interrupted publication completion."); };
        ExpectFailure(() => f.Admin.Update(f.Request()));
        Require(!f.Runtime.Current.PvpEnabled && f.Settings.Current is null && (f.Settings.Pending is not null) == (phase != "intent"),
            "Publication failed without retaining its old committed configuration and pending work: " + phase);
        Require((f.Compatibility.Current!.Mods[0].Role == ModCompatibilityRole.Optional) == (phase is "audit" or "complete"),
            "Compatibility writes did not follow the persisted intent: " + phase);
        Require(f.Control.ListAudit().Count == (phase == "complete" ? 1 : 0), "Failed audit save published an in-memory success.");
        slots.Settings.Fail = slots.Compatibility.Fail = slots.Audit.Fail = false; slots.Settings.Before = null;
        if (reopen) f = new Env(slots);
        if (phase == "intent") Require(f.Admin.Update(f.Request()).Result.Accepted, "Initial write retry failed.");
        else
        {
            bool rejected = false;
            try { f.Settings.Replace(f.Desired(), "other", DateTimeOffset.UtcNow); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Pending publication was overwritten by another registry write.");
            f.Changes.Recover();
        }
        Require(f.Runtime.Current.PvpEnabled && f.Settings.Current!.PvpEnabled && f.Settings.Pending is null
            && f.Compatibility.Current!.Mods[0].Role == ModCompatibilityRole.Optional && f.Control.ListAudit().Count == 1,
            "Publication recovery lost configuration, compatibility roles or audit.");
        var writes = (slots.Settings.Writes, slots.Compatibility.Writes, slots.Audit.Writes);
        f.Changes.Recover();
        Require(writes == (slots.Settings.Writes, slots.Compatibility.Writes, slots.Audit.Writes), "Completed publication kept writing during recovery.");
        var final = new Env(slots);
        Require(final.Runtime.Current.PvpEnabled && final.Settings.Pending is null && final.Control.ListAudit().Count == 1,
            "Completed publication did not survive another reopen.");
    }

    private static void MappingAndAudit()
    {
        var f = new Env();
        f.Slots.Settings.Before = _ => Require(Monitor.IsEntered(f.Context.Gate), "Configuration publication escaped the shared mutation gate.");
        f.Runtime.Replace(new(pvpEnabled: false, authenticationDebugMode: true, steamWebApiKey: "private", steamAppId: 123));
        var dto = Edit(f.Desired(), node =>
        {
            node["BankInterestDurationMultiplierCurve"] = "1:1.1234567;7=1.2345678";
            node["MercenaryDurationMultiplierCurve"] = "1:1.7654321";
            node["BankOverduePenaltyStages"] = "1:Raid:1.3456789";
            node["TradePostageSilverPerTile"] = 3;
            node["RaidProtectionHours"] = 1.5;
            node["FixedTradeFees"] = JsonSerializer.SerializeToNode(new[] { new AdminFixedTradeFeeDto("Steel", 2), new AdminFixedTradeFeeDto("steel", 7) });
        });
        Require(f.Admin.Update(new("user", "colony", f.Token, dto)).Result.Accepted, "Configuration update failed.");
        var current = f.Runtime.Current;
        Require(current.BankInterestDurationMultiplierCurve[0].Multiplier == 1.1234567f
            && current.MercenaryDurationMultiplierCurve[0].Multiplier == 1.7654321f,
            "Configuration publication rounded a duration multiplier.");
        Require(current.TradeFeePolicy.FixedFeePerThing["STEEL"] == 7 && current.RaidProtectionDuration == TimeSpan.FromHours(1.5), "Fee deduplication or time units changed.");
        Require(current.AuthenticationDebugMode && current.SteamWebApiKey == "private" && current.SteamAppId == 123
            && !f.Slots.Settings.Read()!.Contains("private", StringComparison.Ordinal), "Administrator DTO overwrote or persisted authentication secrets.");
        string saved = JsonSerializer.Serialize(f.Changes.Current);
        Require(saved == JsonSerializer.Serialize(AdminConfigurationMapping.ToDto(AdminConfigurationMapping.Apply(f.Settings.Current!, current), f.Compatibility.Current)),
            "Runtime and reopened configuration mapping disagree.");
        for (int i = 0; i < 100; i++) f.Control.AddAudit("test", "user", null, i.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(i));
        var before = JsonSerializer.Serialize(f.Control.ListAudit());
        f.Slots.Audit.Fail = true;
        var record = new AdminAuditRecord("retry", "user", null, null, DateTimeOffset.UtcNow);
        ExpectFailure(() => f.Control.EnsureAudit(record));
        Require(before == JsonSerializer.Serialize(f.Control.ListAudit()), "Audit trimming changed memory before a failed save.");
        f.Slots.Audit.Fail = false; f.Control.EnsureAudit(record); f.Control.EnsureAudit(record);
        Require(f.Control.ListAudit().Count == 100 && f.Control.ListAudit().Count(item => item == record) == 1
            && new AdminControlRegistry(f.Slots.Audit).ListAudit().Count(item => item == record) == 1, "Audit retry lost or duplicated a trimmed record.");
    }

    private static void Status()
    {
        var f = new Env();
        f.Context.Recovery.OnPrerequisites = () => throw new Exception("Status triggered unrelated recovery.");
        f.Context.Players.Record("z-online", "z", "snap-z", DateTimeOffset.UtcNow, "在线");
        f.Context.Players.Record("a-offline", "a", "snap-a", DateTimeOffset.UtcNow, "离线");
        f.World.PromoteAdministrator("a-offline"); f.Control.Ban("a-offline"); f.Control.SetMaintenanceLoginLocked(true, "维护");
        f.Context.Presence.TryConnectExclusive("z-online", out var online);
        using (online)
        {
            var result = f.Admin.Status(new("user", "colony", f.Token));
            Require(result.Result.Accepted && result.Configuration is not null && result.Players.Select(p => p.UserId).SequenceEqual(["z-online", "a-offline"])
                && result.Players[1].IsAdministrator && result.Players[1].IsBanned && result.MaintenanceReason == "维护", "Status lost player order, roles or maintenance data.");
        }
        var denied = f.Admin.Status(new("user", "colony", "bad"));
        Require(!denied.Result.Accepted && denied.Configuration is null && denied.Players.Count == 0 && denied.AuditRecords.Count == 0, "Denied status disclosed administrator state.");
        Require(!f.Admin.Update(new("user", "colony", f.Token, null)).Result.Accepted, "Missing configuration was accepted.");
    }

    private static void RecoveryChanges(bool replace, bool replay, string change)
    {
        var f = new Env(); f.Context.Recovery.NeedsPreparation = true;
        void Change()
        {
            if (change == "role") { f.World.PromoteAdministrator("other"); f.World.RevokeAdministrator("user"); }
            if (change == "credential")
            {
                if (replace) f.Accounts.ResetPassword("user", "changed", DateTimeOffset.UtcNow, out _);
                else f.Context.Tokens.RevokeForUser("user");
            }
        }
        f.Context.Recovery.OnPrepare = () => { Require(!Monitor.IsEntered(f.Context.Gate), "Configuration recovery prepared under the global gate."); if (!replay) Change(); };
        f.Context.Recovery.OnReplay = () => { Require(Monitor.IsEntered(f.Context.Gate), "Recovery replay escaped the gate."); if (replay) Change(); };
        bool accepted = replace ? f.Override.Replace(f.OverrideRequest()).Result.Accepted : f.Admin.Update(f.Request()).Result.Accepted;
        Require(accepted == (change == "none") && (f.Settings.Current is not null) == (!replace && accepted), "Recovery used obsolete administrator privileges.");
        Require((f.Compatibility.Current!.ManifestId == "replacement") == (replace && accepted), "Rejected override replaced the manifest.");
    }

    private static void OverrideOrdering()
    {
        var f = new Env(); f.Slots.Compatibility.Fail = true;
        ExpectFailure(() => f.Admin.Update(f.Request())); f.Slots.Compatibility.Fail = false;
        Require(f.Override.Replace(f.OverrideRequest()).Result.Accepted && f.Settings.Pending is null && f.Runtime.Current.PvpEnabled,
            "Compatibility override did not finish an earlier configuration publication.");
        f.Changes.Recover();
        Require(f.Compatibility.Current!.ManifestId == "replacement" && f.Compatibility.Current.Mods[0].Role == ModCompatibilityRole.Required,
            "Old configuration recovery overwrote a newer compatibility baseline.");
        var denied = f.Override.Replace(new("user", "colony", "{", password: "wrong"));
        Require(!denied.Result.Accepted && denied.ServerCompatibilityManifestJson is null, "Denied override disclosed the compatibility baseline.");
        Require(!f.Override.Replace(new("user", "colony", "{", password: "pw")).Result.Accepted, "Invalid manifest accepted.");
        f.Runtime.Replace(new(authenticationDebugMode: true)); f.Steam.Identity = "user";
        f.Steam.Before = () => Require(!Monitor.IsEntered(f.Context.Gate), "Steam validation ran inside the mutation gate.");
        Require(f.Override.Replace(f.OverrideRequest()).Result.Accepted && f.Steam.Calls == 1, "Steam credentials were not prepared once outside the gate.");
    }

    private static void CorruptPending()
    {
        var f = new Env(); f.Slots.Compatibility.Fail = true; ExpectFailure(() => f.Admin.Update(f.Request()));
        string json = f.Slots.Settings.Read()!;
        var node = JsonNode.Parse(json)!; node["Pending"]!["Version"] = 999; f.Slots.Settings.Write(node.ToJsonString());
        bool failed = false; try { _ = new ServerConfigurationRegistry(f.Slots.Settings); } catch (InvalidDataException) { failed = true; }
        Require(failed, "Unsupported pending publication was silently discarded.");
        f.Slots.Settings.Write("{broken"); failed = false;
        try { _ = new ServerConfigurationRegistry(f.Slots.Settings); } catch (JsonException) { failed = true; }
        Require(failed, "Malformed configuration storage was silently replaced with defaults.");
    }

    private static void PersistentFailure(string store, string phase)
        => WithStorage((root, db) =>
        {
            IJsonPersistenceSlot Open(string name) => store == "sqlite" ? new SqliteJsonPersistenceSlot(db, name) : new FileJsonPersistenceSlot(Path.Combine(root, name + ".json"));
            PublicationFailure(new(new(Open("settings")), new(Open("compatibility")), new(Open("audit"))), phase, true);
        });

    private static void RegistryReplacement()
    {
        var f = new Env(); var now = DateTimeOffset.UnixEpoch;
        f.Settings.Replace(f.Changes.Current, "original", now);
        var old = f.Settings.Current;
        f.Slots.Settings.Fail = true; ExpectFailure(() => f.Settings.Replace(f.Desired(), "new", now.AddDays(1)));
        Require(ReferenceEquals(old, f.Settings.Current) && f.Settings.UpdatedByUserId == "original" && f.Settings.UpdatedAtUtc == now,
            "Direct settings replacement published unpersisted metadata.");
        f.Slots.Settings.Fail = false;
        var node = JsonNode.Parse(f.Slots.Settings.Read()!)!.AsObject(); node.Remove("Pending");
        f.Slots.Settings.Write(node.ToJsonString());
        var oldFormat = new ServerConfigurationRegistry(f.Slots.Settings);
        Require(!oldFormat.Current!.PvpEnabled && oldFormat.Pending is null && oldFormat.UpdatedByUserId == "original", "Legacy configuration format no longer loads.");
    }

    private static void StructuredAuditFailure(bool trim) => WithStorage((_, db) =>
    {
        AdminControlRegistry Open() => new(new SqliteKeyedJsonRecordStore(db, "admin-control"), null);
        var slots = new Slots(new(new SqliteJsonPersistenceSlot(db, "settings")), new(new SqliteJsonPersistenceSlot(db, "compatibility")), new());
        var f = new Env(slots, Open());
        if (trim) for (int i = 0; i < 100; i++) f.Control.AddAudit("seed", "user", null, i.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(i));
        var before = JsonSerializer.Serialize(f.Control.ListAudit());
        void Sql(string sql) { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        Sql("create trigger fail_admin_audit before insert on server_keyed_json_records when new.collection_key = 'admin-control' begin select raise(abort, 'audit failure'); end;");
        bool failed = false; try { f.Admin.Update(f.Request()); } catch (SqliteException) { failed = true; }
        Require(failed && f.Settings.Pending is not null && before == JsonSerializer.Serialize(f.Control.ListAudit())
            && before == JsonSerializer.Serialize(Open().ListAudit()), "Structured audit failure changed cache or lost publication recovery.");
        Sql("drop trigger fail_admin_audit;");
        f = new Env(slots, Open()); f.Changes.Recover(); f.Changes.Recover();
        Require(f.Runtime.Current.PvpEnabled && f.Settings.Pending is null
            && Open().ListAudit().Count(item => item.ActionKind == "UpdateConfiguration") == 1,
            "Structured audit recovery failed or duplicated a record.");
    });

    private static void WithStorage(Action<string, string> run)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cor-admin-settings-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            string db = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(db);
            run(root, db);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Require(Path.GetDirectoryName(root)!.TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                && Path.GetFileName(root).StartsWith("cor-admin-settings-", StringComparison.Ordinal), "Unexpected test cleanup path.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void StartupRecovery()
    {
        var f = new Env(); f.Slots.Audit.Fail = true; ExpectFailure(() => f.Admin.Update(f.Request())); f.Slots.Audit.Fail = false;
        var state = new ClashOfRimNetworkState(serverConfiguration: new(pvpEnabled: false), serverConfigurationOverrides: new(f.Slots.Settings),
            compatibilityBaselineRegistry: new(f.Slots.Compatibility), adminControl: new(f.Slots.Audit));
        Require(state.ServerConfiguration.PvpEnabled && state.ServerConfigurationOverrides.Pending is null && state.AdminControl.ListAudit().Count == 1,
            "Startup did not recover a pending configuration publication.");
    }

    private static void HttpFlows(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string kind in new[] { "status", "update", "override" }) foreach (bool valid in new[] { false, true })
        {
            var accounts = new OfflineAccountRegistry(); accounts.Authenticate("receiver", "pw", DateTimeOffset.UtcNow, true);
            using var f = new HttpSnapshotRecoveryTests.Fixture(factory, accounts: accounts); f.Start(); f.State.WorldConfiguration.Prepare("receiver"); f.Seed();
            string before = f.Evidence();
            var dto = Edit(f.State.ConfigurationChanges.Current, node => node["PvpEnabled"] = false);
            using var response = kind switch
            {
                "status" => f.Post(ProtocolMessageKind.AdminStatus, new AdminStatusRequest("receiver", "away", valid ? f.Token : "bad")),
                "update" => f.Post(ProtocolMessageKind.AdminUpdateConfiguration, new AdminUpdateConfigurationRequest("receiver", "away", valid ? f.Token : "bad", dto)),
                _ => f.Post(ProtocolMessageKind.OverrideCompatibilityBaseline, new OverrideCompatibilityBaselineRequest("receiver", "away", JsonSerializer.Serialize(EntryTestSupport.Manifest()), password: valid ? "pw" : "bad"))
            };
            response.EnsureSuccessStatusCode();
            var body = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            Require(body["result"]!["accepted"]!.GetValue<bool>() == valid, "HTTP administration authorization failed: " + kind);
            if (valid && kind != "status") f.AssertRecovered(); else f.AssertPending(before, kind);
        }
    }

    private sealed record Slots(RecoveryTestSupport.Slot Settings, RecoveryTestSupport.Slot Compatibility, RecoveryTestSupport.Slot Audit)
    { internal Slots() : this(new(), new(), new()) { } }

    private sealed class Env
    {
        internal readonly SessionModuleTestContext Context = new();
        internal readonly Slots Slots;
        internal readonly WorldConfigurationRegistry World = new(WorldConfigurationExtensionService.Empty);
        internal readonly CompatibilityBaselineRegistry Compatibility;
        internal readonly ServerConfigurationRegistry Settings;
        internal readonly ServerConfigurationState Runtime;
        internal readonly AdminControlRegistry Control;
        internal readonly OfflineAccountRegistry Accounts = new();
        internal readonly RecoveryTestSupport.SteamValidator Steam = new();
        internal readonly AdminConfigurationChanges Changes;
        internal readonly AdministratorConfiguration Admin;
        internal readonly CompatibilityBaselineOverride Override;
        internal readonly string Token;
        internal Env(Slots? slots = null, AdminControlRegistry? control = null)
        {
            Slots = slots ?? new(); Settings = new(Slots.Settings); Compatibility = new(Slots.Compatibility); Control = control ?? new(Slots.Audit);
            Compatibility.EnsureBaseline(EntryTestSupport.Manifest() with { ManifestId = "initial", Mods = [new ModManifestEntry { PackageId = "test", Role = ModCompatibilityRole.Required }] }, "user", DateTimeOffset.UnixEpoch);
            var initial = new ClashOfRimServerConfiguration(pvpEnabled: false);
            Runtime = new(Settings.Current is { } current ? AdminConfigurationMapping.Apply(current, initial) : initial);
            Changes = new(Runtime, Settings, Compatibility, Control, Context.Mutations);
            Context.Recovery.OnPrerequisites = Changes.Recover;
            World.Prepare("user"); Token = Context.Issue().Token; Accounts.Authenticate("user", "pw", DateTimeOffset.UtcNow, true);
            Admin = new(new AdministratorAuthorization(Context.Tokens, Context.Sessions, World), Context.Mutations, Changes,
                World, Context.Players, Context.Presence, Control, () => NullLogger.Instance);
            var accounts = new AccountService(Accounts, Context.Tokens, Steam, () => Runtime.Current);
            Override = new(accounts, World, Compatibility, new CompatibilityHandshakeService(Compatibility, World, () => Runtime.Current.CompatibilityOptions), Context.Mutations, () => NullLogger.Instance);
        }
        internal AdminConfigurationDto Desired() => Edit(Changes.Current, node => { node["PvpEnabled"] = true; node["CompatibilityMods"]![0]!["Role"] = "Optional"; });
        internal AdminUpdateConfigurationRequest Request() => new("user", "colony", Token, Desired());
        internal OverrideCompatibilityBaselineRequest OverrideRequest() => new("user", "colony", JsonSerializer.Serialize(EntryTestSupport.Manifest() with
            { ManifestId = "replacement", Mods = [new ModManifestEntry { PackageId = "new", Role = ModCompatibilityRole.Required }] }), password: "pw");
    }
    private static AdminConfigurationDto Edit(AdminConfigurationDto dto, Action<JsonNode> change)
    { var node = JsonSerializer.SerializeToNode(dto)!; change(node); return node.Deserialize<AdminConfigurationDto>()!; }
    private static void ExpectFailure(Action run) { try { run(); } catch (IOException) { return; } throw new Exception("Expected persistence failure missing."); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
