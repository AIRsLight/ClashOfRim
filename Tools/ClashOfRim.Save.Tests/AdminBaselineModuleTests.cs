using System.Net.Http.Json;
using System.Globalization;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Admin;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Trades;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class AdminBaselineModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        int count = 0;
        var culture = CultureInfo.CurrentCulture; var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                foreach (string mode in new[] { "healthy", "immediate", "lazy", "null", "malformed", "duplicates", "separators" })
                { Requirements(mode); count++; }
                foreach (bool royalty in new[] { false, true }) foreach (bool odyssey in new[] { false, true })
                { DlcRequirements(royalty, odyssey); count++; }
                foreach (bool submit in new[] { false, true })
                {
                    foreach (string invalid in new[] { "protocol", "identity", "token", "role", "replacement" })
                    { Unauthorized(submit, invalid); count++; }
                    foreach (bool replay in new[] { false, true }) foreach (string change in new[] { "none", "token", "role" })
                    { RecoveryBoundary(submit, replay, change); count++; }
                }
                Submission(); count++;
                SaveFailure(); count++;
                RecoveryChangesOrders(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture; }
        ProviderIsolationHttp(factory); count++;
        Console.WriteLine($"PASS: administrator baseline module ({count} scenarios)");
    }

    private static void ProviderIsolationHttp(Func<string, SaveSnapshotPackage> factory)
    {
        var plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.baseline", "test", "1", "test", "", [])
        {
            AdminBaselineRequirementProviders = [new Provider(_ => Broken()),
                new Provider(_ => [new("healthy", "prices", DisplayName: "健康插件")])]
        }]);
        using var f = new HttpSnapshotRecoveryTests.Fixture(factory, plugins);
        f.Start(); f.State.WorldConfiguration.Prepare("receiver");
        using var response = f.Post(ProtocolMessageKind.GetAdminBaselineRequirements,
            new GetAdminBaselineRequirementsRequest(ProtocolApiVersion.Current, "receiver", "away", f.Token));
        response.EnsureSuccessStatusCode();
        var result = response.Content.ReadFromJsonAsync<GetAdminBaselineRequirementsResponse>().GetAwaiter().GetResult()!;
        Require(result.Result.Accepted && result.BaselineExtensions.Count == 1
            && result.BaselineExtensions[0].ProviderId == "healthy", "A failing optional provider aborted or polluted administrator requirements.");
    }

    private static void Requirements(string mode)
    {
        var providers = new IAdminBaselineRequirementProvider[]
        {
            new Provider(_ => mode switch
            {
                "immediate" => throw new InvalidOperationException("Injected immediate requirement failure."),
                "lazy" => Broken(),
                "null" => null!,
                "malformed" => [null!, new(" ", "kind"), new("id", " ")],
                "duplicates" => [new("HEALTHY", "PRICES", DisplayName: "older")],
                "separators" => [new("a\u001fb", "c"), new("a", "b\u001fc")],
                _ => []
            }),
            new Provider(_ => [new("healthy", "prices", "依赖包", "本地化名称")])
        };
        var f = new Fixture(new(adminBaselineRequirementProviders: providers));
        var result = f.Module.Requirements(f.Query());
        Require(result.Result.Accepted && result.IsAdministrator && !result.BaselineConfigured
            && result.AdministratorUserId == "user", "Initial administrator status was changed.");
        Require(result.BaselineExtensions.Count == (mode == "separators" ? 3 : 1), "Requirement filtering/deduplication failed: " + mode);
        var healthy = result.BaselineExtensions.Single(item => item.ProviderId == "healthy");
        Require(healthy.DisplayName == "本地化名称" && healthy.RequiredPackageId == "依赖包", "Requirement metadata was changed.");
        Require(f.Context.Players.List().Count == 0 && f.Slot.Writes == 0, "Requirements query published player activity or a baseline.");
    }

    private static void DlcRequirements(bool royalty, bool odyssey)
    {
        var plugins = new ServerPluginRegistry().WithBuiltInPlugins([RoyaltyServerCompatibility.Descriptor, OdysseyServerCompatibility.Descriptor]);
        var f = new Fixture(plugins);
        var mods = new List<ModManifestEntry>();
        if (royalty) mods.Add(new() { PackageId = RoyaltyCompatibilityKeys.PackageId.ToUpperInvariant() });
        if (odyssey) mods.Add(new() { PackageId = OdysseyCompatibilityKeys.PackageId.ToUpperInvariant() });
        f.Compatibility.ReplaceBaseline(EntryTestSupport.Manifest() with { Mods = mods }, "user", DateTimeOffset.UtcNow);
        var result = f.Module.Requirements(f.Query());
        Require(result.BaselineExtensions.Count == (royalty ? 1 : 0) + (odyssey ? 1 : 0), "DLC requirements did not follow the current package set.");
        f.Compatibility.ReplaceBaseline(EntryTestSupport.Manifest() with { Mods = [] }, "user", DateTimeOffset.UtcNow);
        Require(f.Module.Requirements(f.Query()).BaselineExtensions.Count == 0, "Old DLC requirements survived a baseline replacement.");
    }

    private static void Unauthorized(bool submit, string invalid)
    {
        int calls = 0;
        var f = new Fixture(new(adminBaselineRequirementProviders: [new Provider(_ => { calls++; return []; })]));
        if (invalid == "role") f.RevokeRole();
        if (invalid == "replacement")
        { f.Context.Sessions.EndUser("user"); f.Context.Issue(); }
        f.Context.Recovery.OnPrerequisites = () => throw new Exception("Rejected request started recovery.");
        var protocol = invalid == "protocol" ? "old" : ProtocolApiVersion.Current;
        var user = invalid == "identity" ? "" : "user";
        var token = invalid == "token" ? "bad" : f.Token;
        ProtocolResponse result;
        if (submit)
        {
            var response = f.Module.Submit(new(protocol, user, "colony", DateTimeOffset.UnixEpoch, [], [], authToken: token));
            Require(!response.IsAdministrator && response.StandardMarketValueCount == 0, "Rejected submission exposed success metadata.");
            result = response.Result;
        }
        else
        {
            var response = f.Module.Requirements(new(protocol, user, "colony", token));
            Require(!response.IsAdministrator && response.BaselineExtensions.Count == 0, "Rejected query exposed requirements.");
            result = response.Result;
        }
        Require(!result.Accepted && f.Baseline.Current is null && f.Slot.Writes == 0 && calls == 0, "Unauthorized request reached baseline business.");
    }

    private static void RecoveryBoundary(bool submit, bool replay, string change)
    {
        var f = new Fixture();
        f.Context.Recovery.NeedsPreparation = true;
        void Change()
        {
            if (change == "token") f.Context.Tokens.RevokeForUser("user");
            if (change == "role") f.RevokeRole();
        }
        f.Context.Recovery.OnPrepare = () =>
        {
            Require(!Monitor.IsEntered(f.Context.Gate), "Administrator recovery preparation held the mutation gate.");
            if (!replay) Change();
        };
        f.Context.Recovery.OnReplay = () =>
        {
            Require(Monitor.IsEntered(f.Context.Gate), "Administrator recovery replay lost the mutation gate.");
            if (replay) Change();
        };
        bool accepted = submit ? f.Module.Submit(f.Request()).Result.Accepted : f.Module.Requirements(f.Query()).Result.Accepted;
        Require(accepted == (change == "none") && (f.Baseline.Current is not null) == (submit && accepted),
            "Recovery did not revalidate administrator access: " + change);
        Require(f.Context.Recovery.Replays == (change == "none" || replay ? 1 : 0), "Rejected preparation still replayed changes.");
    }

    private static void Submission()
    {
        var f = new Fixture();
        f.Slot.Before = _ => Require(Monitor.IsEntered(f.Context.Gate), "Baseline was published outside the trade mutation gate.");
        var result = f.Module.Submit(f.Request([new("Steel", 4), new("steel", 12.25f), new("Silver", 1), new("", 9), new("bad", -1)]));
        Require(result.Result.Accepted && result.IsAdministrator && result.BaselineConfigured && result.StandardMarketValueCount == 2,
            "Submission did not return normalized counts.");
        Require(f.Baseline.Current!.StandardMarketValuePerThing["STEEL"] == 12.25f
            && f.Baseline.Current.GeneratedAtUtc == DateTimeOffset.UnixEpoch, "Submission changed prices or source time.");
        var activity = f.Context.Players.FindByUserId("user");
        Require(activity?.ColonyId == "colony" && activity.LastSeenAtUtc > DateTimeOffset.UnixEpoch, "Successful submission did not record activity.");
        Require(f.Module.Requirements(f.Query()).BaselineConfigured, "Requirements query did not observe the saved baseline.");
    }

    private static void SaveFailure()
    {
        var f = new Fixture();
        Require(f.Module.Submit(f.Request()).Result.Accepted, "Initial baseline failed.");
        var before = f.Baseline.Current; var player = f.Context.Players.FindByUserId("user");
        var fees = f.Baseline.BuildEffectiveTradeFeePolicy(f.Context.Configuration);
        f.Slot.Fail = true;
        ExpectFailure(() => f.Module.Submit(f.Request([new("Silver", 2)])));
        Require(ReferenceEquals(before, f.Baseline.Current) && ReferenceEquals(fees, f.Baseline.BuildEffectiveTradeFeePolicy(f.Context.Configuration))
            && Equals(player, f.Context.Players.FindByUserId("user")), "Failed save changed baseline, fee cache or activity.");
        f.Slot.Fail = false;
        var reopened = new Fixture(slot: f.Slot);
        Require(reopened.Baseline.Current!.StandardMarketValuePerThing["Silver"] == 1, "Failed baseline was durable after reopen.");
        Require(reopened.Module.Submit(reopened.Request([new("Silver", 2)])).Result.Accepted
            && new AdminBaselineRegistry(f.Slot).Current!.StandardMarketValuePerThing["Silver"] == 2, "Retry did not publish the baseline.");
    }

    private static void RecoveryChangesOrders()
    {
        var f = new Fixture();
        var order = AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony"), new("server"), "new-order", false,
            new TradeEventPayload("new-order", TradeStage.MarketOrder, [new("steel", "Steel", 10)], [new("silver", "Silver", 20)], 0), DateTimeOffset.UtcNow);
        f.Context.Recovery.OnReplay = () => f.Context.Ledger.Append(order);
        var response = f.Module.Submit(f.Request());
        Require(response.Result.Accepted && f.Context.Ledger.Find(order.EventId)!.Status == ServerEventStatus.Cancelled,
            "Baseline publication missed an order created by recovery.");
        Require(f.Context.Ledger.ListByType(ServerEventType.ItemDelivery).Count == 1 && f.Baseline.ListPendingTradeInvalidations().Count == 0,
            "Baseline replacement lost the refund or retained a completed invalidation.");
        f.Context.Recovery.OnReplay = null;
        Require(f.Module.Submit(f.Request()).Result.Accepted && f.Context.Ledger.ListByType(ServerEventType.ItemDelivery).Count == 1,
            "Repeated submission duplicated a refund.");
    }

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext Context = new();
        internal readonly RecoveryTestSupport.Slot Slot;
        internal readonly WorldConfigurationRegistry World = new(WorldConfigurationExtensionService.Empty);
        internal readonly CompatibilityBaselineRegistry Compatibility = new();
        internal readonly AdminBaselineRegistry Baseline;
        internal readonly AdministratorBaseline Module;
        internal readonly string Token;
        internal Fixture(ServerPluginRegistry? plugins = null, RecoveryTestSupport.Slot? slot = null)
        {
            Slot = slot ?? new(); Baseline = new(Slot); plugins ??= new();
            World.Prepare("user"); Token = Context.Issue().Token;
            var delivery = new TradeDelivery(Context.Snapshots, Context.Presence, Context.Ledger, plugins, Compatibility,
                World, new StraightLineWorldTileDistanceCalculator(), () => Context.Configuration);
            var trades = new TradeLifecycle(Context.Ledger, Context.Snapshots, Context.Mutations, Context.Gate, delivery, Baseline,
                Context.Presence, Context.Events, NullLogger.Instance, () => Context.Configuration);
            Context.Recovery.OnPrerequisites = trades.RecoverAdminBaselineTradeInvalidations;
            Module = new(new AdministratorAuthorization(new SessionPrincipalAuthorization(Context.Tokens, Context.Sessions), World), World, Compatibility, plugins,
                Baseline, trades, Context.Mutations, Context.Activity);
        }
        internal GetAdminBaselineRequirementsRequest Query() => new(ProtocolApiVersion.Current, "user", "colony", Token);
        internal SubmitAdminBaselineRequest Request(IReadOnlyList<StandardMarketValueDto>? prices = null)
            => new(ProtocolApiVersion.Current, "user", "colony", DateTimeOffset.UnixEpoch, prices ?? [new("Silver", 1)], [], authToken: Token);
        internal void RevokeRole() { World.PromoteAdministrator("other"); Require(World.RevokeAdministrator("user"), "Fixture did not revoke administrator."); }
    }

    private static IEnumerable<AdminBaselineExtensionRequirement> Broken()
    {
        yield return new("broken", "partial");
        throw new InvalidOperationException("Injected delayed requirement failure.");
    }
    internal sealed class Provider(Func<AdminBaselineRequirementContext, IEnumerable<AdminBaselineExtensionRequirement>> read)
        : IAdminBaselineRequirementProvider
    {
        public IEnumerable<AdminBaselineExtensionRequirement> GetRequirements(AdminBaselineRequirementContext context) => read(context);
    }
    private static void ExpectFailure(Action run) { try { run(); } catch (IOException) { return; } throw new Exception("Expected save failure missing."); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
