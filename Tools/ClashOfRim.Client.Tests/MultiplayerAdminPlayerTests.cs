using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Multiplayer;

internal static class MultiplayerAdminPlayerTests
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
                await Projection();
                foreach (ClientAdminPlayerAction kind in Enum.GetValues(typeof(ClientAdminPlayerAction))) await Command(kind);
            }
            foreach (string change in new[] { "close", "user", "colony", "auth", "server", "login", "permission", "permission-return",
                "generation", "unavailable", "reset", "removed", "target-colony" }) await Stale(change);
            await TargetFlags(); await Busy(); await PasswordLifecycle(); await LargeRoster();
            foreach (string failure in new[] { "network", "rejected", "exception" }) await Failure(failure);
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Projection()
    {
        var f = await Fixture(Player("alice"), Player("I"), Player("ı"), Player("no-colony", ""), Player(""));
        var model = Model(f); var rows = model.Read();
        Check(ReferenceEquals(rows, model.Read()) && rows.Count == 5 && rows[0].IsSelf && !model.CanManage(rows[0])
            && model.PrepareActions(rows[0]).Count == 0 && model.PrepareActions(rows[4]).Count == 0, "Projection rebuilt each frame or exposed self/empty-account controls.");
        Check(rows[1].UserId == "I" && rows[2].UserId == "ı" && !ReferenceEquals(rows[1], rows[2]), "Culture-sensitive player identity matching merged targets.");
        Check(model.PrepareActions(rows[3]).All(intent => intent.Kind != ClientAdminPlayerAction.DeletePlayerSave), "Blank colony enabled deletion with a later server fallback.");
        Check(model.PrepareActions(rows[1]).Select(intent => intent.Kind).SequenceEqual(new[] { ClientAdminPlayerAction.Kick, ClientAdminPlayerAction.Ban,
            ClientAdminPlayerAction.PromoteAdmin, ClientAdminPlayerAction.DeletePlayerSave, ClientAdminPlayerAction.ResetOfflinePassword }), "Player menu changed its supported action set.");
        f.ReadResponse.Players[1].DisplayName = "   "; f.ReadResponse.Players[1].Online = true;
        await Refresh(f); var next = model.Read();
        Check(ReferenceEquals(rows[1], next[1]) && next[1].DisplayName == "I" && next[1].Online, "Ordinary refresh replaced target identity or lost presentation updates.");
        f.ReadResponse.Players[1].UserId = "transport-mutated";
        Check(next[1].UserId == "I" && f.Module.Snapshot!.Players[1].UserId == "I", "Transport DTO escaped into a target.");
    }
    private static async Task Command(ClientAdminPlayerAction kind)
    {
        var player = Player("target"); player.IsBanned = kind == ClientAdminPlayerAction.Unban; player.IsAdministrator = kind == ClientAdminPlayerAction.RevokeAdmin;
        var f = await Fixture(player); var model = Model(f); var intent = model.PrepareActions(model.Read()[0]).Single(i => i.Kind == kind);
        var sent = new List<ClientAdminCommand>(); f.ObserveCommand = sent.Add;
        f.Change("snapshot"); f.Change("game"); f.Access.SetAdministrator(true);
        Task? work = intent.ExecuteAsync(kind == ClientAdminPlayerAction.ResetOfflinePassword ? "密碼 İ " : null);
        Check(work is not null && intent.ExecuteAsync() is null, "Current action was blocked or dispatched twice.");
        model.Close(); await f.Ports.PumpUntil(work!);
        var command = sent.Single();
        Check(command.Kind == kind.ToString() && command.User == "target" && command.Colony == "colony" && command.Persistent
            && command.Message == (kind == ClientAdminPlayerAction.ResetOfflinePassword ? "密碼 İ " : null), "Action changed target, kind or fields.");
        Check(f.Calls.Select(call => call.Operation).SequenceEqual(new[] { "read", "action", "read" })
            && f.Calls.ElementAt(1).Context.CurrentSnapshotId == "new" && f.Notices.Count == 1 && !f.Access.Busy,
            "Page close canceled accepted work, lost current context or duplicated the shared follow-up refresh.");
    }
    private static async Task Stale(string change)
    {
        var f = await Fixture(Player("target")); var model = Model(f); var row = model.Read()[0];
        var intents = model.PrepareActions(row); var password = model.OpenPassword(intents.Last())!; password.Value = "not-for-next-session";
        int calls = f.Calls.Count;
        switch (change)
        {
            case "close": model.Close(); break;
            case "permission-return": f.Access.SetAdministrator(false); f.Access.SetAdministrator(true); await Refresh(f); calls = f.Calls.Count; break;
            case "reset": f.Module.Reset(); break;
            case "removed": f.ReadResponse.Players.Clear(); await Refresh(f); model.Read(); f.ReadResponse.Players.Add(Player("target")); await Refresh(f); calls = f.Calls.Count; break;
            case "target-colony": f.ReadResponse.Players[0].ColonyId = "rebuilt"; await Refresh(f); calls = f.Calls.Count; break;
            default: f.Change(change); break;
        }
        foreach (var intent in intents) Check(!intent.IsCurrent && intent.ExecuteAsync() is null, "Old action crossed " + change);
        Check(password.Value == "" && !password.CanSubmit && password.Submit() is null && !model.CanManage(row)
            && f.Calls.Count == calls && !f.Access.Busy, "Old password/target retained state after " + change);
    }
    private static async Task TargetFlags()
    {
        var f = await Fixture(Player("target")); var model = Model(f); var row = model.Read()[0];
        var original = model.PrepareActions(row); var kick = original.Single(i => i.Kind == ClientAdminPlayerAction.Kick);
        var ban = original.Single(i => i.Kind == ClientAdminPlayerAction.Ban); var promote = original.Single(i => i.Kind == ClientAdminPlayerAction.PromoteAdmin);
        var password = model.OpenPassword(original.Last())!; password.Value = "preserved";
        f.ReadResponse.Players[0].IsBanned = true; await Refresh(f); model.Read();
        Check(!ban.IsCurrent && kick.IsCurrent && promote.IsCurrent && password.Value == "preserved", "Ban change affected unrelated account actions or kept a stale ban intent.");
        f.ReadResponse.Players[0].IsBanned = false; await Refresh(f); model.Read();
        Check(!ban.IsCurrent && model.PrepareActions(row).Any(i => i.Kind == ClientAdminPlayerAction.Ban && i.IsCurrent), "Ban/unban round-trip revived an old menu.");
        f.ReadResponse.Players[0].IsAdministrator = true; await Refresh(f); model.Read();
        Check(!promote.IsCurrent && kick.IsCurrent, "Role change kept the old promotion or invalidated unrelated kick.");
        f.ReadResponse.Players[0].IsAdministrator = false; await Refresh(f); model.Read();
        Check(!promote.IsCurrent, "Role round-trip revived an old menu.");
        f.ReadResponse.Players[0].Online = true; f.ReadResponse.Players[0].CurrentSnapshotId = "advanced";
        f.ReadResponse.Players[0].LastSeenAtUtc = "later"; f.ReadResponse.Players[0].DisplayName = "renamed"; await Refresh(f);
        Check(kick.IsCurrent && password.Value == "preserved", "Display/presence/save refresh discarded an account intent.");
        password.Close();
    }
    private static async Task Busy()
    {
        var f = await Fixture(Player("target")); var model = Model(f); var intents = model.PrepareActions(model.Read()[0]);
        var password = model.OpenPassword(intents.Last())!; password.Value = "first";
        var ticket = f.Access.Begin(out _)!; Check(ticket is not null, "Cannot occupy administrator admission.");
        foreach (var intent in intents) Check(intent.IsCurrent && !intent.CanExecute && intent.ExecuteAsync() is null, "Busy action was consumed or bypassed shared admission.");
        password.Value = "next"; Check(!password.CanSubmit && password.Submit() is null && password.Value == "next", "Busy password submission consumed edits.");
        f.Access.End(ticket!); ClientAdminCommand? sent = null; f.ObserveCommand = command => sent = command;
        Task? work = password.Submit(); Check(work is not null && password.Value == "" && password.Submit() is null, "Password dispatch did not clear its draft exactly once.");
        await f.Ports.PumpUntil(work!); Check(sent?.Message == "next", "Explicit password retry lost the latest input.");
    }
    private static async Task PasswordLifecycle()
    {
        var f = await Fixture(Player("target")); var model = Model(f); var intents = model.PrepareActions(model.Read()[0]);
        Check(model.OpenPassword(intents[0]) is null, "Non-password intent opened a credential draft.");
        var closed = model.OpenPassword(intents.Last())!; closed.Value = "old"; closed.Close(); closed.Value = "late";
        Check(closed.Value == "" && closed.Submit() is null, "Closed password draft retained input or submitted.");
        foreach (string value in new[] { "", " 密碼 İ " })
        {
            var draft = model.OpenPassword(model.PrepareActions(model.Read()[0]).Last())!; draft.Value = value;
            ClientAdminCommand? sent = null; f.ObserveCommand = command => sent = command;
            Task? work = draft.Submit(); Check(work is not null, "UI changed server-owned password validation.");
            draft.Value = "after-dispatch"; await f.Ports.PumpUntil(work!);
            Check(sent?.Message == value && draft.Value == "", "Password was trimmed, changed in flight or retained after dispatch.");
        }
    }
    private static async Task Failure(string failure)
    {
        var f = await Fixture(Player("target")); var model = Model(f); var commands = new List<ClientAdminCommand>(); f.ObserveCommand = commands.Add;
        f.FaultOperation = "action"; f.Fault = failure;
        var intent = model.PrepareActions(model.Read()[0])[0]; await f.Ports.PumpUntil(intent.ExecuteAsync()!);
        Check(!f.Access.Busy && f.Notices.IsEmpty && !intent.IsCurrent && f.Calls.Count == 2, "Failed action retained admission or refreshed as success: " + failure);
        f.Fault = null; await f.Ports.PumpUntil(model.PrepareActions(model.Read()[0])[0].ExecuteAsync()!);
        Check(commands.Count == 2 && commands.All(command => command.Kind == "Kick" && command.User == "target") && f.Notices.Count == 1,
            "Explicit new action could not recover after " + failure);
    }
    private static async Task LargeRoster()
    {
        var f = await Fixture(Enumerable.Range(0, 1500).Select(i => Player(i.ToString(CultureInfo.InvariantCulture))).ToArray()); var model = Model(f); var rows = model.Read();
        for (int i = 0; i < 20; i++) Check(ReferenceEquals(rows, model.Read()), "Large player list rebuilt per frame.");
        f.ReadResponse.Players.Reverse(); await Refresh(f); var reordered = model.Read();
        Check(reordered.Count == 1500 && ReferenceEquals(rows[0], reordered[1499]) && ReferenceEquals(rows[1499], reordered[0]), "Refresh duplicated targets or ignored server ordering.");
    }
    private static async Task<ClientAdministrationTests.Fixture> Fixture(params ModAdminPlayerSummaryDto[] players)
    { var f = new ClientAdministrationTests.Fixture(); f.ReadResponse.Players = players.ToList(); await Refresh(f); return f; }
    private static Task Refresh(ClientAdministrationTests.Fixture f) => f.Ports.PumpUntil(f.Module.RefreshAsync());
    private static ClientAdminPlayerPanel Model(ClientAdministrationTests.Fixture f) => new(f.Session, f.Access, f.Module);
    private static ModAdminPlayerSummaryDto Player(string user, string colony = "colony") => new() { UserId = user, ColonyId = colony, DisplayName = "玩家 " + user };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
