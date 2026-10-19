using AIRsLight.ClashOfRim.Modules.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using Verse;

internal static class MultiplayerAdminHostTests
{
    internal static void Run() => RunAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); await Schedule(); await Navigation(); }
            foreach (string change in new[] { "close", "user", "server", "colony", "auth", "login", "generation", "permission", "permission-return", "unavailable" }) await Ownership(change);
            foreach (string fault in new[] { "network", "rejected", "exception" }) await Failure(fault);
            await InFlight(); await Denied(); await HostClose(); Cache();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static ClientAdminPanel Panel(ClientAdministrationTests.Fixture f) => new(f.Session, f.Access, f.Module);
    private static async Task Schedule()
    {
        var f = new ClientAdministrationTests.Fixture(); var model = Panel(f);
        Check(model.IsAvailable && model.CanRefresh && model.Selected == ClientAdminTab.Trade && model.Status == f.Module.Message, "Initial host status/navigation changed.");
        var held = f.Access.Begin(out _)!;
        await model.RefreshAsync(100); await model.RefreshAsync(100, manual: true);
        Check(f.Calls.IsEmpty && !model.CanRefresh, "Busy host admitted a refresh.");
        f.Access.End(held); await f.Ports.PumpUntil(model.RefreshAsync(100));
        Check(f.Calls.Count == 1 && model.Status.Contains("StatusRefreshed"), "Busy tick consumed the pending refresh or lost status.");
        await model.RefreshAsync(100); await model.RefreshAsync(129.9f);
        Check(f.Calls.Count == 1, "Automatic refresh ignored its shared interval.");
        await f.Ports.PumpUntil(model.RefreshAsync(110, manual: true));
        await model.RefreshAsync(139.9f); Check(f.Calls.Count == 2, "Manual refresh left the old automatic deadline.");
        await f.Ports.PumpUntil(model.RefreshAsync(140)); Check(f.Calls.Count == 3, "Automatic refresh missed the new deadline.");
        model.RequestRefresh(); held = f.Access.Begin(out _)!; await model.RefreshAsync(140); f.Access.End(held);
        await f.Ports.PumpUntil(model.RefreshAsync(140)); Check(f.Calls.Count == 4, "Requested refresh was lost while admission was busy.");
        var snapshot = f.Module.Snapshot; var owner = model.Ownership; model.Select(ClientAdminTab.Audit)(); model.Close();
        Check(ReferenceEquals(snapshot, f.Module.Snapshot) && !ReferenceEquals(owner, model.Ownership) && model.Selected == ClientAdminTab.Audit,
            "Closing host cleared domain data/navigation preference or retained its scope.");
        await f.Ports.PumpUntil(model.RefreshAsync(140)); Check(f.Calls.Count == 5, "Reopening inherited the closed page's refresh timer.");
    }
    private static async Task Navigation()
    {
        var f = new ClientAdministrationTests.Fixture(); var model = Panel(f);
        await f.Ports.PumpUntil(model.RefreshAsync(100));
        var editor = new ClientAdminConfigurationEditor(f.Session, f.Access, f.Module); var draft = editor.Read()!;
        var players = new ClientAdminPlayerPanel(f.Session, f.Access, f.Module);
        var maintenance = new ClientAdminMaintenancePanel(f.Session, players, f.Module); var announcement = maintenance.Read();
        draft.TradeOrderExpirationDays.Text = "unfinished"; announcement.Message = "next announcement";
        foreach (ClientAdminTab tab in Enum.GetValues(typeof(ClientAdminTab)))
        {
            model.Select(tab)(); await model.RefreshAsync(101);
            Check(model.Selected == tab && f.Calls.Count == 1 && ReferenceEquals(draft, editor.Read()) && ReferenceEquals(announcement, maintenance.Read()),
                "Subtab navigation refreshed shared data or discarded another page's edits: " + tab);
        }
        var owner = model.Ownership; var held = f.Access.Begin(out _)!;
        model.Select(ClientAdminTab.Bank)(); Check(model.Selected == ClientAdminTab.Bank, "Busy admission blocked local navigation."); f.Access.End(held);
        f.Change("game"); f.Change("snapshot"); f.Access.SetAdministrator(true);
        Check(ReferenceEquals(owner, model.Ownership) && model.Selected == ClientAdminTab.Bank && draft.TradeOrderExpirationDays.Text == "unfinished"
            && announcement.Message == "next announcement", "Game/snapshot or repeated role observation reset account UI state.");
        await model.RefreshAsync(129.9f); Check(f.Calls.Count == 1, "Account-independent changes reset refresh scheduling.");
    }
    private static async Task Ownership(string change)
    {
        var f = new ClientAdministrationTests.Fixture(); var model = Panel(f); await f.Ports.PumpUntil(model.RefreshAsync(100));
        model.Select(ClientAdminTab.Bank)(); var oldSelect = model.Select(ClientAdminTab.Audit); var owner = model.Ownership;
        if (change == "close") model.Close();
        else if (change == "permission-return") { f.Access.SetAdministrator(false); f.Access.SetAdministrator(true); }
        else f.Change(change);
        oldSelect();
        Check(model.Selected == ClientAdminTab.Bank && !ReferenceEquals(owner, model.Ownership), "Old navigation crossed " + change);
        if (!model.IsAvailable)
        {
            Check(model.Status == "" && !model.CanRefresh, "Unavailable host exposed old status under " + change);
            await model.RefreshAsync(101, manual: true); Check(f.Calls.Count == 1, "Unavailable host sent a request under " + change);
        }
        if (change == "unavailable") f.Session.PublishLogin(new() { AuthenticatedUserId = f.Ports.User, AuthToken = f.Ports.Auth, SessionId = "fresh" });
        f.Access.SetAdministrator(true);
        oldSelect(); Check(model.Selected == ClientAdminTab.Bank, "Regrant revived old navigation under " + change);
        await f.Ports.PumpUntil(model.RefreshAsync(101)); Check(f.Calls.Count == 2, "New page ownership inherited an old deadline under " + change);
        model.Select(ClientAdminTab.Players)(); Check(model.Selected == ClientAdminTab.Players, "Current navigation did not recover under " + change);
    }
    private static async Task InFlight()
    {
        var f = new ClientAdministrationTests.Fixture(); var model = Panel(f);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Before = async _ => { entered.TrySetResult(); await release.Task; };
        Task work = model.RefreshAsync(100); await entered.Task;
        Check(!model.CanRefresh && model.Status.Contains("StatusRefreshing"), "Host copied busy/status instead of observing the shared operation.");
        for (int i = 0; i < 20; i++) { await model.RefreshAsync(200); await model.RefreshAsync(200, manual: true); }
        model.Close(); model.RequestRefresh(); await model.RefreshAsync(200);
        Check(f.Calls.Count == 1, "Busy ticks or reopening started duplicate status reads.");
        f.Before = null; release.SetResult(); await f.Ports.PumpUntil(work);
        Check(model.CanRefresh && f.Module.Snapshot is not null, "Closing a host canceled an admitted domain read.");
        await f.Ports.PumpUntil(model.RefreshAsync(200)); Check(f.Calls.Count == 2, "Reopened host lost its deferred refresh.");
        await f.Ports.PumpUntil(f.Start("action"));
        Check(f.Calls.Select(c => c.Operation).SequenceEqual(new[] { "read", "read", "action", "read" }), "Host duplicated the action's existing follow-up refresh.");
        await model.RefreshAsync(201); Check(f.Calls.Count == 4, "Host added an extra read immediately after action completion.");
    }
    private static async Task Failure(string fault)
    {
        var f = new ClientAdministrationTests.Fixture { FaultOperation = "read", Fault = fault }; var model = Panel(f);
        await f.Ports.PumpUntil(model.RefreshAsync(100));
        Check(model.CanRefresh && f.Calls.Count == 1 && model.Status == f.Module.Message, "Failure retained host busy state or hid the domain result: " + fault);
        await model.RefreshAsync(101); Check(f.Calls.Count == 1, "Failed refresh retried every frame.");
        f.Fault = null; await f.Ports.PumpUntil(model.RefreshAsync(101, manual: true));
        Check(f.Calls.Count == 2 && f.Module.Snapshot is not null, "Manual refresh could not retry a failure.");
    }
    private static async Task Denied()
    {
        var f = new ClientAdministrationTests.Fixture(); var model = Panel(f); f.ReadResponse.IsAdministrator = false;
        await f.Ports.PumpUntil(model.RefreshAsync(100));
        Check(!model.IsAvailable && !model.CanRefresh && model.Status == "", "Server-revoked administrator kept an active host.");
        await model.RefreshAsync(200, manual: true); Check(f.Calls.Count == 1, "Revoked host kept polling.");
        f.ReadResponse.IsAdministrator = true; f.Access.SetAdministrator(true);
        await f.Ports.PumpUntil(model.RefreshAsync(101)); Check(model.IsAvailable && f.Calls.Count == 2, "New grant did not reset refresh eligibility.");
    }
    private static void Cache()
    {
        var f = new ClientAdministrationTests.Fixture(); var model = Panel(f); var cache = new MultiplayerTabCache<(ClientAdminTab, bool)>();
        int builds = 0; object language = new(); var callbacks = new List<Action>();
        List<TabRecord> Build()
        {
            builds++; var select = model.Select(ClientAdminTab.Audit); callbacks.Add(select);
            return new() { new("tab-" + builds, select, false) };
        }
        var rows = cache.Read(model.Ownership, (ClientAdminTab.Trade, false), language, Build);
        Check(ReferenceEquals(rows, cache.Read(model.Ownership, (ClientAdminTab.Trade, false), language, Build)) && builds == 1, "Stable tab strip rebuilt every frame.");
        language = new(); var translated = cache.Read(model.Ownership, (ClientAdminTab.Trade, false), language, Build);
        Check(!ReferenceEquals(rows, translated) && builds == 2, "Language change retained old tab labels.");
        model.Close(); var rebound = cache.Read(model.Ownership, (ClientAdminTab.Trade, false), language, Build);
        Check(!ReferenceEquals(translated, rebound) && builds == 3, "Equal-valued new scope retained old tab callbacks.");
        callbacks[0](); Check(model.Selected == ClientAdminTab.Trade, "Cached old tab affected reopened host.");
        callbacks[2](); Check(model.Selected == ClientAdminTab.Audit, "Rebound tab did not target the current host.");
        _ = cache.Read(model.Ownership, (ClientAdminTab.Audit, false), language, Build);
        _ = cache.Read(model.Ownership, (ClientAdminTab.Audit, true), language, Build);
        Check(builds == 5, "Selection or visibility change retained an obsolete tab list.");
        bool threw = false;
        try { cache.Read(model.Ownership, (ClientAdminTab.Bank, true), language, () => throw new InvalidOperationException("expected")); }
        catch (InvalidOperationException) { threw = true; }
        _ = cache.Read(model.Ownership, (ClientAdminTab.Bank, true), language, Build);
        Check(threw && builds == 6, "Failed tab construction published its key with a previous list.");
        cache.Clear(); _ = cache.Read(model.Ownership, (ClientAdminTab.Bank, true), language, Build);
        Check(builds == 7, "Explicit tab cache cleanup retained old UI state.");
        var equalOwners = new MultiplayerTabCache<int>();
        var first = equalOwners.Read(new CacheOwner(1), 0, language, Build);
        var second = equalOwners.Read(new CacheOwner(1), 0, language, Build);
        Check(!ReferenceEquals(first, second), "Value-equal owners reused another instance's tab callbacks.");
    }
    private static async Task HostClose()
    {
        using var operations = new ClientShopOperationsTests.Fixture(false);
        using var catalog = new ClientShopCatalogTests.Fixture(operations.Ports, operations.Session, operations.State, operations.Mutations);
        var f = new ClientAdministrationTests.Fixture(operations.Ports, operations.Session);
        f.ReadResponse.Players.Add(new() { UserId = "target", ColonyId = "home" });
        f.ReadResponse.AuditRecords.Add(new() { ActorUserId = "alice", ActionKind = "previous" });
        await f.Ports.PumpUntil(f.Module.RefreshAsync()); await catalog.Load(operations.Listing);
        var model = Panel(f); var configuration = new ClientAdminConfigurationEditor(f.Session, f.Access, f.Module);
        var players = new ClientAdminPlayerPanel(f.Session, f.Access, f.Module);
        var maintenance = new ClientAdminMaintenancePanel(f.Session, players, f.Module);
        var audit = new ClientAdminAuditPanel(f.Session, f.Access, f.Module);
        var shop = new ClientShopPanel(f.Session, f.Ports, operations.State, catalog.Catalog, operations.Operations, operations.Sync);
        var adminShop = new ClientAdminShopPanel(f.Session, f.Access, operations.State, catalog.Catalog, shop);
        var compatibility = new ClientCompatibilityOverrideTests.Fixture(f); var world = new ClientWorldWritesTests.Fixture(ClientWorldWriteKind.Baseline, f);
        var manifest = new ClientAdminManifestPanel(configuration, new OrderSource(), compatibility.Flow, world.Module, () => null);
        var page = new MultiplayerAdminPage(model, configuration, new(configuration), new(adminShop), new(players), new(maintenance), new(manifest), new(audit));
        var draft = configuration.Read()!; draft.TradeOrderExpirationDays.Text = "local edit";
        var announcement = maintenance.Read(); announcement.Message = "local announcement";
        var target = players.Read().Single(p => p.UserId == "target");
        var password = players.OpenPassword(players.PrepareActions(target).Single(i => i.Kind == ClientAdminPlayerAction.ResetOfflinePassword))!;
        password.Value = "secret";
        var edit = adminShop.PrepareEdit(adminShop.Read()[0]); var baseline = manifest.PrepareOverride(draft)!;
        var sharedShop = shop.Read(); var shopOwner = shop.Ownership; var accepted = f.Module.Snapshot; var auditRows = audit.Read();
        var oldNavigation = model.Select(ClientAdminTab.Audit);
        page.Close(); oldNavigation();
        Check(model.Selected == ClientAdminTab.Trade && page.IsAvailable && !configuration.IsCurrent(draft)
            && !ReferenceEquals(announcement, maintenance.Read()) && !baseline.CanStart && password.Value == "" && !password.CanSubmit && !edit.IsCurrent,
            "Host close missed a child draft, password, baseline or shop edit.");
        Check(ReferenceEquals(accepted, f.Module.Snapshot) && ReferenceEquals(shopOwner, shop.Ownership) && ReferenceEquals(sharedShop, shop.Read()),
            "Closing administrator host reset shared server/shop data or player shop ownership.");
        Check(!ReferenceEquals(auditRows, audit.Read()) && audit.Read().Single().ActionKind == "previous", "Host close retained audit projection or cleared accepted records.");
        await f.Ports.PumpUntil(model.RefreshAsync(100)); Check(f.Calls.Count == 2, "Closed host could not refresh on reopen.");
    }
    private sealed class OrderSource : IClientCompatibilityModOrderSource
    {
        public string SteamPostfix => "_steam";
        public IReadOnlyList<string> ReadPackageOrder() => new[] { "测试 İ" };
    }
    private sealed record CacheOwner(int Id);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
