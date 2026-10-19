using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Multiplayer;

internal static class MultiplayerAdminMaintenanceTests
{
    internal static void Run() => RunAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Targets();
                foreach (ClientAdminBroadcastSeverity severity in Enum.GetValues(typeof(ClientAdminBroadcastSeverity)))
                foreach (bool persistent in new[] { false, true })
                foreach (bool targeted in new[] { false, true }) await Broadcast(severity, persistent, targeted);
            }
            foreach (string change in new[] { "close", "players-close", "user", "server", "colony", "auth", "login", "generation", "permission", "permission-return", "unavailable" }) await Stale(change);
            await RemovedTarget(); await ColonyChange(); await Maintenance(); await Busy(); await IndependentDrafts();
            foreach (string kind in new[] { "broadcast", "lock", "unlock" })
            foreach (string fault in new[] { "network", "rejected", "exception" }) await Failure(kind, fault);
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Targets()
    {
        var f = await Fixture(Player("alice", "自己"), Player("I", "same"), Player("ı", "same"), Player("İ", "  "), Player("I", "same", "second-colony"), Player("", "invalid"));
        var rows = f.Players.Read(); var accounts = f.Players.ReadAccounts(); var options = f.Model.Targets();
        Check(accounts.Count == 4 && options.Count == 4 && ReferenceEquals(accounts, f.Players.ReadAccounts()), "Account projection duplicated users or rebuilt per read.");
        Check(ReferenceEquals(rows[1].Account, rows[4].Account) && ReferenceEquals(rows[1].Account, options.Single(a => a.UserId == "I"))
            && !ReferenceEquals(rows[1].Account, rows[2].Account), "Broadcast did not share exact account identities with player management.");
        Check(options.Single(a => a.UserId == "İ").DisplayName == "İ", "Empty account label lost its identity fallback.");
        for (int i = 1; i < options.Count; i++)
            Check(StringComparer.CurrentCultureIgnoreCase.Compare(options[i - 1].DisplayName, options[i].DisplayName) <= 0, "Target menu ignored current culture ordering.");
        var draft = f.Model.Read();
        Check(draft.Message == "" && draft.Reason == "" && draft.Persistent && draft.Severity == ClientAdminBroadcastSeverity.Info && draft.Target is null, "Default announcement/maintenance draft changed.");
        f.Model.ChooseTarget(draft)(rows[0].Account); Check(f.Model.CanBroadcast(draft), "Own account was excluded from notifications.");
        f.Model.ChooseTarget(draft)(rows[5].Account); Check(!f.Model.CanBroadcast(draft), "Blank account widened delivery to all users.");
        f.Model.ChooseSeverity(draft)((ClientAdminBroadcastSeverity)999); Check(draft.Severity == ClientAdminBroadcastSeverity.Info, "Unknown severity did not use the existing Info fallback.");
        f.Model.ChooseTarget(draft)(null); Check(f.Model.CanBroadcast(draft), "Explicit all-player selection remained blocked.");
    }
    private static async Task Broadcast(ClientAdminBroadcastSeverity severity, bool persistent, bool targeted)
    {
        var f = await Fixture(Player("alice", "自己"), Player("target", "目标")); var draft = f.Model.Read();
        draft.Message = "  公告 İ \u001f  "; draft.Persistent = persistent; f.Model.ChooseSeverity(draft)(severity);
        if (targeted) f.Model.ChooseTarget(draft)(f.Model.Targets().Single(a => a.UserId == "target"));
        ClientAdminCommand? sent = null; f.Backend.ObserveCommand = command => sent = command;
        f.Backend.Change("game"); f.Backend.Change("snapshot"); f.Backend.Access.SetAdministrator(true);
        Check(ReferenceEquals(draft, f.Model.Read()), "Game/snapshot advance discarded an account draft.");
        Task? work = f.Model.BroadcastAsync(draft); Check(work is not null, "Valid broadcast did not dispatch.");
        draft.Message = "next"; draft.Persistent = !persistent; f.Model.ChooseSeverity(draft)(ClientAdminBroadcastSeverity.Warning); f.Model.ChooseTarget(draft)(null);
        Check(f.Model.BroadcastAsync(draft) is null && f.Model.SetMaintenanceAsync(draft, true) is null, "Shared busy request admitted another command.");
        await f.Backend.Ports.PumpUntil(work!);
        Check(sent is { Kind: "Broadcast", Colony: null } && sent.User == (targeted ? "target" : null) && sent.Message == "  公告 İ \u001f  "
            && sent.Severity == severity.ToString() && sent.Persistent == persistent, "Broadcast routing/fields were changed by later edits.");
        Check(f.Backend.Calls.Select(c => c.Operation).SequenceEqual(new[] { "read", "action", "read" })
            && f.Backend.Calls.ElementAt(1).Context.CurrentSnapshotId == "new" && f.Backend.Notices.Count == 1
            && ReferenceEquals(draft, f.Model.Read()) && draft.Message == "next", "Successful broadcast duplicated refresh or consumed the next draft.");
    }
    private static async Task RemovedTarget()
    {
        var f = await Fixture(Player("target", "原目标")); var draft = f.Model.Read(); var target = f.Model.Targets()[0];
        var choose = f.Model.ChooseTarget(draft); draft.Message = "private";
        // A menu opened while this was a valid account is clicked after its removal.
        f.Backend.ReadResponse.Players.Clear(); await f.Refresh(); choose(target);
        Check(ReferenceEquals(draft.Target, target) && !f.Model.CanBroadcast(draft) && f.Model.BroadcastAsync(draft) is null,
            "Missing target silently widened a delayed selection to everyone.");
        f.Backend.ReadResponse.Players.Add(Player("target", "新目标")); await f.Refresh();
        var replacement = f.Model.Targets()[0];
        Check(!ReferenceEquals(target, replacement) && !f.Model.CanBroadcast(draft), "A reappearing account revived an old selection.");
        choose(replacement); Check(f.Model.CanBroadcast(draft), "Explicit replacement selection failed.");
        choose(null); Check(draft.Target is null && f.Model.CanBroadcast(draft), "Explicit broadcast-to-all failed after target loss.");
        Check(f.Backend.Calls.All(call => call.Operation == "read"), "Rejected target selection sent a network command.");
    }
    private static async Task ColonyChange()
    {
        var f = await Fixture(Player("target", "before", "old-colony")); var row = f.Players.Read()[0];
        var deletion = f.Players.PrepareActions(row).Single(i => i.Kind == ClientAdminPlayerAction.DeletePlayerSave);
        var draft = f.Model.Read(); f.Model.ChooseTarget(draft)(row.Account);
        f.Backend.ReadResponse.Players[0].ColonyId = "rebuilt"; f.Backend.ReadResponse.Players[0].DisplayName = "renamed"; await f.Refresh();
        Check(!deletion.IsCurrent && !ReferenceEquals(row, f.Players.Read()[0]) && ReferenceEquals(draft.Target, f.Players.Read()[0].Account)
            && draft.Target!.DisplayName == "renamed" && f.Model.CanBroadcast(draft), "Account routing borrowed colony ownership or weakened destructive targeting.");
        ClientAdminCommand? sent = null; f.Backend.ObserveCommand = command => sent = command;
        await f.Backend.Ports.PumpUntil(f.Model.BroadcastAsync(draft)!);
        Check(sent is { User: "target", Colony: null }, "Account broadcast retained an obsolete colony constraint.");
    }
    private static async Task Maintenance()
    {
        var backend = new ClientAdministrationTests.Fixture(); var players = new ClientAdminPlayerPanel(backend.Session, backend.Access, backend.Module);
        var model = new ClientAdminMaintenancePanel(backend.Session, players, backend.Module); var draft = model.Read();
        Check(model.LoginLocked is null, "Missing server status was reported as unlocked.");
        await backend.Ports.PumpUntil(backend.Module.RefreshAsync()); Check(model.LoginLocked == false, "Accepted unlocked state was not displayed.");
        draft.Reason = "  维护原因 İ  "; draft.Message = "unused announcement"; draft.Persistent = false;
        ClientAdminCommand? sent = null; backend.ObserveCommand = command => sent = command;
        foreach (bool locked in new[] { true, false, true })
        {
            backend.ReadResponse.MaintenanceLoginLocked = locked; backend.ReadResponse.MaintenanceReason = "server text";
            Task? work = model.SetMaintenanceAsync(draft, locked); Check(work is not null, "Explicit maintenance command was rejected.");
            await backend.Ports.PumpUntil(work!);
            Check(sent!.Kind == (locked ? "LockMaintenance" : "UnlockMaintenance") && sent.User is null && sent.Colony is null
                && sent.Severity is null && sent.Persistent && sent.Message == (locked ? "  维护原因 İ  " : null)
                && model.LoginLocked == locked && draft.Reason == "  维护原因 İ  ", "Maintenance mixed announcement fields, inferred a toggle or overwrote the local reason.");
        }
    }
    private static async Task Stale(string change)
    {
        var f = await Fixture(Player("target", "目标")); var draft = f.Model.Read(); draft.Message = "old"; draft.Reason = "old";
        var chooseTarget = f.Model.ChooseTarget(draft); var chooseSeverity = f.Model.ChooseSeverity(draft); var target = f.Model.Targets()[0];
        switch (change)
        {
            case "close": f.Model.Close(); break; case "players-close": f.Players.Close(); break;
            case "permission-return": f.Backend.Access.SetAdministrator(false); f.Backend.Access.SetAdministrator(true); await f.Refresh(); break;
            default: f.Backend.Change(change); break;
        }
        var current = f.Model.Read(); int calls = f.Backend.Calls.Count;
        chooseTarget(target); chooseSeverity(ClientAdminBroadcastSeverity.Critical);
        Check(!ReferenceEquals(draft, current) && current.Target is null && current.Message == "" && current.Reason == ""
            && current.Severity == ClientAdminBroadcastSeverity.Info && draft.Target is null && draft.Severity == ClientAdminBroadcastSeverity.Info,
            "Old menu/draft crossed " + change);
        Check(f.Model.BroadcastAsync(draft) is null && f.Model.SetMaintenanceAsync(draft, true) is null && f.Model.SetMaintenanceAsync(draft, false) is null
            && f.Backend.Calls.Count == calls && !f.Backend.Access.Busy, "Old form submitted under " + change);
    }
    private static async Task Busy()
    {
        var f = await Fixture(Player("target", "目标")); var draft = f.Model.Read(); var ticket = f.Backend.Access.Begin(out _)!;
        f.Model.ChooseTarget(draft)(f.Model.Targets()[0]); f.Model.ChooseSeverity(draft)(ClientAdminBroadcastSeverity.Critical);
        draft.Message = "pending"; draft.Reason = "reason";
        Check(f.Model.BroadcastAsync(draft) is null && f.Model.SetMaintenanceAsync(draft, true) is null && ReferenceEquals(f.Model.Read(), draft)
            && draft.Severity == ClientAdminBroadcastSeverity.Critical && draft.Target is not null, "Busy admission discarded local edits or allowed a command.");
        f.Backend.Access.End(ticket); ClientAdminCommand? sent = null; f.Backend.ObserveCommand = command => sent = command;
        Task work = f.Model.BroadcastAsync(draft)!; f.Model.Close(); await f.Backend.Ports.PumpUntil(work);
        Check(sent is { User: "target", Message: "pending", Severity: "Critical" } && f.Backend.Notices.Count == 1 && !f.Backend.Access.Busy
            && f.Model.Read().Message == "", "Close canceled admitted work or preserved the old form.");
    }
    private static async Task IndependentDrafts()
    {
        var f = await Fixture(Player("target", "目标")); var draft = f.Model.Read(); draft.Message = "retained";
        f.Model.ChooseTarget(draft)(f.Model.Targets()[0]); var identity = draft.Target;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); await f.Refresh();
        Check(ReferenceEquals(draft, f.Model.Read()) && ReferenceEquals(identity, draft.Target) && f.Model.CanBroadcast(draft), "Culture/identical refresh discarded a draft.");
        var second = new ClientAdminMaintenancePanel(f.Backend.Session, f.Players, f.Backend.Module); var other = second.Read(); other.Message = "second";
        Check(f.Model.BroadcastAsync(other) is null && second.BroadcastAsync(draft) is null, "One page submitted another page's draft.");
        second.Close(); Check(draft.Message == "retained" && f.Model.CanBroadcast(draft), "Closing one form reset the other form/shared targets.");
    }
    private static async Task Failure(string kind, string fault)
    {
        var f = await Fixture(Player("target", "目标")); var draft = f.Model.Read(); draft.Message = "retry"; draft.Reason = "retry reason";
        f.Backend.ObserveCommand = _ => { }; f.Backend.FaultOperation = "action"; f.Backend.Fault = fault;
        Task? Start() => kind == "broadcast" ? f.Model.BroadcastAsync(draft) : f.Model.SetMaintenanceAsync(draft, kind == "lock");
        await f.Backend.Ports.PumpUntil(Start()!);
        Check(!f.Model.Busy && ReferenceEquals(draft, f.Model.Read()) && draft.Message == "retry" && draft.Reason == "retry reason"
            && f.Backend.Notices.IsEmpty && f.Backend.Calls.Count == 2, "Failed " + kind + "/" + fault + " consumed edits or performed a success refresh.");
        f.Backend.Fault = null; await f.Backend.Ports.PumpUntil(Start()!);
        Check(f.Backend.Notices.Count == 1 && f.Backend.Calls.Count == 4, "Explicit retry failed or duplicated follow-up work.");
    }
    private static ModAdminPlayerSummaryDto Player(string user, string label, string colony = "colony") => new() { UserId = user, DisplayName = label, ColonyId = colony };
    private static async Task<FixtureState> Fixture(params ModAdminPlayerSummaryDto[] players)
    {
        var backend = new ClientAdministrationTests.Fixture(); backend.ReadResponse.Players = players.ToList();
        var roster = new ClientAdminPlayerPanel(backend.Session, backend.Access, backend.Module);
        var fixture = new FixtureState(backend, roster, new(backend.Session, roster, backend.Module)); await fixture.Refresh(); return fixture;
    }
    private sealed record FixtureState(ClientAdministrationTests.Fixture Backend, ClientAdminPlayerPanel Players, ClientAdminMaintenancePanel Model)
    { internal Task Refresh() => Backend.Ports.PumpUntil(Backend.Module.RefreshAsync()); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
