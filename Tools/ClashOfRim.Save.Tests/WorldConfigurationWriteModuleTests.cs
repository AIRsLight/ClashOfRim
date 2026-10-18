using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

internal static class WorldConfigurationWriteModuleTests
{
    internal static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture;
        int count = 0; void Case(Action action) { action(); count++; }
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string coverage in new[] { "NaN", "Infinity", "-Infinity", "1e100", "0", "-0.3", "0,3", "0.3", "30", "1e-1" }) Case(() => Coverage(coverage));
                foreach (string mode in new[] { "normal", "normalize-token", "normalize-policy", "recovery-token", "recovery-failure", "persistence-failure" }) Case(() => Submission(mode));
                foreach (string mode in new[] { "success", "protocol", "password", "world", "empty", "fingerprint", "near", "far", "nan", "infinity", "persistence-failure" }) Case(() => Names(mode));
                foreach (string mode in new[] { "success", "protocol", "missing", "wrong-user", "wrong-colony", "password", "token", "revoked", "replaced", "nonadmin", "world", "payload", "persistence-failure" }) Case(() => Substrate(mode));
                Case(Transport);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Console.WriteLine($"World write independent module scenarios passed: {count}");
    }

    private static void Coverage(string coverage)
    {
        var f = new Fixture(); f.Ready();
        string before = f.Evidence();
        bool valid = coverage is "0.3" or "30" or "1e-1";
        var response = f.Submission.Submit(f.Request(Configuration(coverage)));
        Check(response.Result.Accepted == valid, "Wrong coverage result: " + coverage);
        if (valid)
            Check(f.World.Current is null && !f.World.TryGetWorldSubstrate("world", out _) && f.Notifications.GetVersion("user") == 0,
                "New baseline exposed old substrate or notified before readiness.");
        else
        {
            Check(f.Evidence() == before && f.S.Recovery.Replays == 0 && f.S.PlayerSlot.Writes == 0,
                "Invalid coverage recovered or changed world/activity: " + coverage);
            f.World.Submit("user", Configuration(coverage));
            Check(f.Evidence() == before && f.Reopen().Current!.PlanetCoverage == "0.3", "Registry bypassed the shared baseline rule: " + coverage);
        }
    }

    private static void Submission(string mode)
    {
        var f = new Fixture(); f.Ready(); string before = f.Evidence();
        f.Provider.BeforeNormalize = () =>
        {
            Check(!Monitor.IsEntered(f.S.Gate), "Extension normalization held the server gate.");
            if (mode == "normalize-token") f.S.Tokens.RevokeForUser("user");
            if (mode == "normalize-policy") f.Baseline.ReplaceBaseline(EntryTestSupport.Manifest("changed"), "user", DateTimeOffset.UtcNow);
        };
        f.S.Recovery.NeedsPreparation = true;
        f.S.Recovery.OnPrepare = () =>
        {
            Check(!Monitor.IsEntered(f.S.Gate), "World recovery prepared under the server gate.");
            if (mode == "recovery-token") f.S.Tokens.RevokeForUser("user");
            if (mode == "recovery-failure") throw new IOException("Injected preparation failure.");
        };
        f.Slot.Before = _ => Check(Monitor.IsEntered(f.S.Gate), "World submission published outside the mutation scope.");
        f.Slot.Fail = mode == "persistence-failure";
        SubmitWorldConfigurationResponse? result = null; bool threw = false;
        try { result = f.Submission.Submit(f.Request(Configuration(id: "next"))); } catch (IOException) { threw = true; }
        Check(threw == (mode is "recovery-failure" or "persistence-failure"), "Unexpected world publication exception: " + mode);
        if (!threw) Check(result!.Result.Accepted == (mode == "normal"), "Prepared world published after authority changed: " + mode);
        if (mode != "normal") Check(f.Evidence() == before, "Failed submission changed persisted world or sent notifications.");
        f.Provider.BeforeNormalize = null; f.S.Recovery.OnPrepare = null; f.Slot.Before = null; f.Slot.Fail = false;
        f.Token = f.S.Issue().Token;
        var retry = f.Submission.Submit(f.Request(Configuration(id: "next")));
        Check(retry.Result.Accepted && retry.IsAdministrator && !retry.WorldConfigured && retry.WorldConfiguration is null
            && f.S.Players.FindByUserId("user")?.ColonyId == "colony" && !Monitor.IsEntered(f.S.Gate), "World retry did not finish cleanly.");
        Check(f.Substrate.Upload(f.UploadRequest("next"), Payload(), f.Token).Result.Accepted, "Normalized baseline did not accept matching substrate.");
        var stored = f.Reopen().Current!;
        Check(stored.ConfiguredByUserId == "user" && stored.ConfiguredByColonyId == "colony"
            && stored.GameLanguage == "German" && stored.DifficultyValuesXml!.Contains("2.35", StringComparison.Ordinal), "Submission lost normalized ownership or runtime settings.");
    }

    private static void Names(string mode)
    {
        var f = new Fixture(); f.Ready();
        f.S.Players.Record("user", "colony", null, DateTimeOffset.UtcNow);
        f.S.Players.Record("watcher", "watch", null, DateTimeOffset.UtcNow);
        string before = f.Evidence(); f.Slot.Fail = mode == "persistence-failure";
        var feature = Feature(mode == "fingerprint" ? "Other" : "Mountain", "montagna");
        float x = mode switch { "near" => 1.50005f, "far" => 1.5005f, "nan" => float.NaN, "infinity" => float.PositiveInfinity, _ => feature.DrawCenterX };
        feature = new(feature.DefName, feature.Label, feature.MaxDrawSizeInTiles, x, feature.DrawCenterY, feature.DrawCenterZ);
        var request = new SubmitWorldFeatureNamesRequest(mode == "protocol" ? "bad" : ProtocolApiVersion.Current,
            "user", "colony", " Italian ", mode == "world" ? "other" : "world",
            mode == "empty" ? [] : [feature], password: mode == "password" ? "bad" : "pw");
        SubmitWorldFeatureNamesResponse? response = null; bool threw = false;
        try { response = f.Names.Submit(request); } catch (IOException) { threw = true; }
        Check(threw == (mode == "persistence-failure"), "Name catalog failure changed.");
        if (!threw) Check(response!.Result.Accepted == (mode is "success" or "near") && response.Created == (mode is "success" or "near"), "Wrong catalog acceptance: " + mode);
        if (mode is not ("success" or "near")) Check(f.Evidence() == before, "Rejected catalog changed state/notification: " + mode);
        f.Slot.Fail = false;
        var valid = new SubmitWorldFeatureNamesRequest(ProtocolApiVersion.Current, "user", "colony", "italian", "world", [Feature("Mountain", "montagna")], password: "pw");
        Check(f.Names.Submit(valid).Result.Accepted, "Name catalog retry failed.");
        string published = f.Evidence();
        var duplicate = f.Names.Submit(new(ProtocolApiVersion.Current, "user", "colony", "ITALIAN", "world", [Feature("Mountain", "overwrite")], password: "pw"));
        var world = f.Reopen().Current!;
        Check(duplicate.Result.Accepted && !duplicate.Created && f.Evidence() == published
            && world.FeatureNameCatalogs.Single(c => c.Language.Equals("Italian", StringComparison.OrdinalIgnoreCase)).Features.Single().Label == "montagna"
            && world.Features.Single().Label == "Berge" && world.GameLanguage == "German"
            && f.Notifications.GetVersion("user") == 1 && f.Notifications.GetVersion("watcher") == 1
            && f.S.Recovery.Replays == 0, "Catalog retry overwrote another language, repeated notification, or started recovery.");
    }

    private static void Substrate(string mode)
    {
        var f = new Fixture(); f.World.Submit("user", Configuration());
        f.S.Players.Record("watcher", "watch", null, DateTimeOffset.UtcNow);
        if (mode == "revoked") f.S.Tokens.RevokeForUser("user");
        if (mode == "replaced") f.S.Issue();
        if (mode == "nonadmin") { f.World.PromoteAdministrator("other"); f.World.RevokeAdministrator("user"); }
        string before = f.Evidence(); f.Slot.Fail = mode == "persistence-failure";
        var request = new UploadWorldSubstrateRequest(mode == "protocol" ? "bad" : ProtocolApiVersion.Current,
            mode == "wrong-user" ? "other" : "user", mode == "wrong-colony" ? "other" : "colony", mode == "world" ? "other" : "world",
            password: mode == "password" ? "bad" : "pw", authToken: f.Token);
        UploadWorldSubstrateResponse? response = null; bool threw = false;
        try { response = f.Substrate.Upload(mode == "missing" ? null : request, mode == "payload" ? [1, 2, 3] : Payload(), mode == "token" ? "bad" : f.Token); }
        catch (IOException) { threw = true; }
        Check(threw == (mode == "persistence-failure"), "Substrate persistence failure changed.");
        if (!threw) Check(response!.Result.Accepted == (mode == "success"), "Wrong substrate acceptance: " + mode);
        if (mode != "success") Check(f.Evidence() == before && f.World.Current is null, "Failed substrate became ready or sent notifications.");
        f.Slot.Fail = false; f.World.PromoteAdministrator("user"); f.Token = f.S.Issue().Token;
        var retried = f.Substrate.Upload(f.UploadRequest(), Payload(), f.Token);
        Check(retried.Result.Accepted && retried.LayerCount == 2 && retried.TileCenterCount == 2 && retried.WorldConfiguration?.TileGeometry is null,
            "Substrate retry lost multi-layer geometry or leaked geometry into response.");
        Check(f.Reopen().Current?.TileGeometry?.Layers.Count == 2 && f.S.Recovery.Replays == 0 && f.S.PlayerSlot.Writes == 1,
            "Substrate upload did not persist layers or invoked unrelated lifecycle/recovery.");
        long expected = mode == "success" ? 2 : 1;
        Check(f.Notifications.GetVersion("user") == expected && f.Notifications.GetVersion("watcher") == expected, "Ready world did not notify owner and known players.");
        foreach (string invalid in new[] { "", "protocol", "password", "world" })
        {
            var download = f.Substrate.Download(new(invalid == "protocol" ? "bad" : ProtocolApiVersion.Current, "user", "colony",
                invalid == "world" ? "other" : "world", password: invalid == "password" ? "bad" : "pw"));
            var expectedStatus = invalid switch { "protocol" => WorldSubstrateDownloadStatus.InvalidRequest, "password" => WorldSubstrateDownloadStatus.Unauthorized,
                "world" => WorldSubstrateDownloadStatus.NotFound, _ => WorldSubstrateDownloadStatus.Available };
            Check(download.Status == expectedStatus && (download.Payload is not null) == (invalid == ""), "Wrong substrate download outcome: " + invalid);
            if (download.Payload is { } payload) { Check(payload.SequenceEqual(Payload()), "Downloaded bytes changed."); payload[0] ^= 0xff; }
        }
        Check(f.Substrate.Download(new(ProtocolApiVersion.Current, "user", "colony", "world", password: "pw")).Payload!.SequenceEqual(Payload()), "Download leaked the mutable registry buffer.");
    }

    private static void Transport()
    {
        var f = new Fixture(); f.World.Submit("user", Configuration());
        var context = new DefaultHttpContext(); context.Request.ContentType = "multipart/form-data; boundary=test";
        context.Request.Headers[MultipartSnapshotAuthentication.HeaderName] = f.Token;
        byte[] bytes = Payload(); using var stream = new MemoryStream(bytes);
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            { ["request"] = JsonSerializer.Serialize(f.UploadRequest(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) },
            new FormFileCollection { new FormFile(stream, 0, bytes.Length, "payload", "world.cors.gz") });
        var result = (IValueHttpResult)WorldEndpoints.Upload(context.Request, f.Substrate).GetAwaiter().GetResult();
        Check(((UploadWorldSubstrateResponse)result.Value!).Result.Accepted, "Multipart endpoint did not carry header, metadata and bytes.");
        var file = (IFileHttpResult)WorldEndpoints.Download(new(ProtocolApiVersion.Current, "user", "colony", "world", password: "pw"), f.Substrate);
        Check(file.ContentType == "application/octet-stream" && file.FileDownloadName == "world-substrate.cors.gz", "Download file contract changed.");
        foreach (var (world, password, protocol, status) in new[] { ("other", "pw", ProtocolApiVersion.Current, 404), ("world", "bad", ProtocolApiVersion.Current, 401), ("world", "pw", "bad", 400) })
            Check(((IStatusCodeHttpResult)WorldEndpoints.Download(new(protocol, "user", "colony", world, password: password), f.Substrate)).StatusCode == status, "Download HTTP status changed.");
        // A token carried in JSON cannot substitute for the authenticated multipart header.
        context.Request.Headers.Remove(MultipartSnapshotAuthentication.HeaderName);
        result = (IValueHttpResult)WorldEndpoints.Upload(context.Request, f.Substrate).GetAwaiter().GetResult();
        Check(!((UploadWorldSubstrateResponse)result.Value!).Result.Accepted, "Upload accepted a body token without a header principal.");
    }

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly RecoveryTestSupport.Slot Slot = new();
        internal readonly WorldConfigurationPersistenceTests.MemoryBinary Binary = new();
        internal readonly WorldConfigurationAuthorizationTests.Provider Provider = new();
        internal readonly CompatibilityBaselineRegistry Baseline = new();
        internal readonly EventNotificationHub Notifications = new();
        internal readonly WorldConfigurationRegistry World;
        internal readonly WorldConfigurationSubmission Submission;
        internal readonly WorldFeatureNames Names;
        internal readonly WorldSubstrateTransfer Substrate;
        internal string Token;
        internal Fixture()
        {
            World = Reopen(); World.Prepare("user"); Token = S.Issue().Token;
            var accounts = new OfflineAccountRegistry(); accounts.Authenticate("user", "pw", DateTimeOffset.UtcNow, true);
            var credentials = new AccountService(accounts, S.Tokens, new RecoveryTestSupport.SteamValidator(), () => S.Configuration);
            var plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.world-auth", "test", "1", "test", "", [], WorldConfigurationExtensionProviders: [Provider])]);
            var delivery = new WorldConfigurationDelivery(plugins, Baseline); var changes = new WorldConfigurationChanges(S.Players, Notifications);
            Submission = new(new(new(S.Tokens, S.Sessions), World), Baseline, plugins, S.Mutations, World, S.Activity, delivery, () => NullLogger.Instance);
            Names = new(credentials, World, delivery, changes, () => NullLogger.Instance);
            Substrate = new(credentials, new(S.Tokens, S.Sessions), World, delivery, changes, () => NullLogger.Instance);
        }
        internal WorldConfigurationRegistry Reopen() => new(Slot, Binary);
        internal void Ready() { World.Submit("user", Configuration()); Check(World.StoreWorldSubstrate("user", "colony", "world", Payload()).Accepted, "Cannot seed ready world."); }
        internal SubmitWorldConfigurationRequest Request(WorldConfigurationDto configuration) => new(ProtocolApiVersion.Current, "user", "colony", configuration, Token);
        internal UploadWorldSubstrateRequest UploadRequest(string id = "world") => new(ProtocolApiVersion.Current, "user", "colony", id, password: "pw", authToken: Token);
        internal string Evidence() => JsonSerializer.Serialize(new { current = World.Current, stored = Slot.Read(), user = Notifications.GetVersion("user"), watcher = Notifications.GetVersion("watcher") });
    }
    private static WorldFeatureDto Feature(string def, string label) => new(def, label, 3.25f, 1.5f, 2.5f, -0.75f);
    private static WorldConfigurationDto Configuration(string coverage = "0.3", string id = "world") => new(id, "spoofed", "spoofed", DateTimeOffset.UnixEpoch,
        "seed", coverage, "Normal", "Normal", "Normal", "Normal", "2", ["OutlanderCivil"], [Feature("Mountain", "Berge")], [], [], [], [],
        "Cassandra", "Custom", difficultyValuesXml: "<saveable><threatScale>2.35</threatScale></saveable>", gameLanguage: "German");
    private static byte[] Payload() => WorldSubstratePackageCodec.Encode(new WorldSubstratePackage(1,
        "<grid><layers><keys><li>0</li><li>2</li></keys><values><li/><li/></values></layers></grid>", "<features/>", "<landmarks/>",
        WorldTileGeometryBinaryCodec.Encode(new WorldTileGeometryDto([new(0, "Surface", 1, [new(0, 1, 0, 0)]), new(2, "Orbit", 1, [new(0, 0, 1, 0)])]))!));
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
