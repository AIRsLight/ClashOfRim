using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Raids;

internal static class ClientRaidCommandTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (string path in new[] { "menu", "vehicle", "transporters" })
            { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); await Success(path); count++; }
            foreach (string phase in new[] { "label", "option-text", "menu", "choose", "confirmation-text", "confirmation", "confirm", "launch" })
            foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "game", "world", "snapshot", "password" })
            { await Ownership(phase, change); count++; }
            await Refusals(); TransporterTargets();
            Console.WriteLine($"Raid commands checked {count} completion/ownership cases plus refusal and target conversion cases.");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Success(string path)
    {
        var f = new Fixture(); f.Core.Fault = "";
        Task work;
        if (path == "menu")
        {
            f.Open();
            Check(f.Options!.Single().Label.Contains("original-label") && f.Options[0].Label.Contains("1"), "Menu lost target/count arguments.");
            f.Core.Target.OwnerUserId = "other"; f.Core.Target.MapId = "other-map"; f.Core.Target.Tile = 100; f.Core.Target.CanRaid = false;
            f.Options[0].Choose(); f.Options[0].Choose();
            Check(f.Confirmations == 1 && f.Prompt!.Contains("original-label"), "Choice repeated confirmation or reread mutable target.");
            work = f.Confirm!(); await f.Confirm();
        }
        else if (path == "vehicle") work = f.Commands.StartVehicle(f.Source, f.Core.Target);
        else
        {
            var target = ClientRaidCommands.TransporterTarget("defender", "there", "old-defender-snapshot", "map", "world", 7, 2, "original-label");
            work = f.Commands.StartTransporters(f.Source, target);
        }
        await f.Core.Open(); (await f.Core.Ports.Next())(); await work;
        Check(f.Core.Completed == 1 && f.Core.Prepared == 1 && f.Core.Captured == 1 && f.Core.Opened == 1 && f.Core.Submissions == 1
            && f.Core.Ports.Published == 1 && f.Core.Ports.GamePublished == 1 && !f.Core.Sync.IsBusy && !f.Core.Mutations.IsPending,
            "Command bypassed or duplicated the launch workflow: " + path);
        Check(f.Core.Sent!.Target == new ClientRaidTarget("defender", "there", "world", "map", 7)
            && f.Core.Sent.DefenderSnapshotId == "downloaded" && f.Core.CapturedTarget!.TileLayerId == 2, "Command changed frozen target/authoritative snapshot.");
        if (path == "menu")
        {
            await f.Confirm!();
            Check(f.Core.Captured == 1, "Completed confirmation launched again after admission released.");
        }
    }
    private static async Task Ownership(string phase, string change)
    {
        var f = new Fixture(); bool changed = false;
        void Change()
        {
            changed = true;
            switch (change)
            {
                case "user": f.Core.Ports.User = "other"; break;
                case "server": f.Core.Ports.Server = "http://other"; break;
                case "colony": f.Core.Ports.Colony = "other"; break;
                case "auth": f.Core.Ports.Auth = "other"; break;
                case "login": f.Core.Session.PublishLogin(new() { AuthToken = "auth", SessionId = "same-credentials" }); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "game": f.Core.Ports.GameOwner = new(); break;
                case "world": f.World = new(); break;
                case "snapshot": f.Core.Ports.Snapshot = "advanced"; break;
                case "password": f.Core.Ports.Password = "changed"; break;
            }
        }
        f.Hook = at => { if (!changed && at == phase) Change(); };
        f.Open();
        if (phase == "choose") Change();
        f.Options?.Single().Choose();
        if (phase == "confirm") Change();
        await f.Core.Ports.PumpUntil(f.Confirm?.Invoke() ?? Task.CompletedTask);
        bool continues = change is "snapshot" or "password";
        Check(changed && f.Core.Prepared == (continues ? 1 : 0) && f.Core.Captured == (continues ? 1 : 0),
            "Command rebound to a new owner or rejected normal progression: " + phase + "/" + change);
        Check(f.Core.Opened == 0 && !f.Core.Sync.IsBusy, "Controlled preparation refusal moved pawns or leaked admission.");
        if (continues) Check(f.Core.PreparationContext!.CurrentSnapshotId == (change == "snapshot" ? "advanced" : "base"), "Launch ignored current valid base.");
    }
    private static async Task Refusals()
    {
        foreach (string fault in new[] { "pvp", "source", "unraidable", "null", "incomplete" })
        {
            var f = new Fixture();
            if (fault == "pvp") f.Core.Fault = "pvp";
            if (fault == "source") f.Available = false;
            if (fault == "unraidable") f.Core.Target.CanRaid = false;
            if (fault == "incomplete") f.Core.Target.MapId = "";
            await f.Commands.StartVehicle(f.Source, fault == "null" ? null : f.Core.Target);
            Check(f.Core.Captured == 0 && f.Core.Prepared == 0 && !f.Core.Sync.IsBusy && !f.Core.Mutations.IsPending,
                "Vehicle refusal reached capture/network: " + fault);
            if (fault is "source" or "unraidable" or "null")
                Check(f.Statuses.Single().Contains(fault == "source" ? "RaidStartFailedNoCaravan" : "RaidUnavailable") && f.Messages.Count == 1,
                    "Vehicle refusal lost its specific feedback.");
        }
        foreach (string reason in new[] { "pvp", "targets", "pawns" })
        {
            var f = new Fixture(); if (reason == "pvp") f.Core.Fault = "pvp"; if (reason == "pawns") f.PawnCount = 0;
            f.Commands.OpenCaravanMenu(f.Source, reason == "targets" ? Array.Empty<ModWorldMapMarkerDto>() : new[] { f.Core.Target });
            Check(f.Options is null && f.Statuses.Count == 1 && f.Messages.Count == (reason == "pvp" ? 1 : 0), "Menu refusal changed status/message behavior.");
        }
        foreach (bool beforeChoice in new[] { false, true })
        foreach (bool disablePvp in new[] { false, true })
        {
            var f = new Fixture(); f.Open();
            if (!beforeChoice) f.Options!.Single().Choose();
            if (disablePvp) f.Core.Fault = "pvp"; else f.Available = false;
            if (beforeChoice) f.Options!.Single().Choose();
            await (f.Confirm?.Invoke() ?? Task.CompletedTask);
            Check(f.Core.Captured == 0 && f.Messages.Count == 1, "Delayed command ignored changed PvP/source availability.");
        }
        foreach (bool missing in new[] { false, true })
        {
            var f = new ClientRaidLaunchTestPorts(); f.Target.CanRaid = false;
            await f.Launch.StartAsync(f.BindLaunch(), missing ? null : f.Target);
            Check(f.Captured == 0 && f.Prepared == 0 && !f.Sync.IsBusy, "Direct launch bypassed target eligibility.");
        }
    }
    private static void TransporterTargets()
    {
        foreach (int layer in new[] { -4, 0, 7 })
        {
            var target = ClientRaidCommands.TransporterTarget("u", "c", "s", "m", "w", 901, layer, "é");
            Check(target is { MarkerId: "", Kind: "TradeableColony", OwnerUserId: "u", OwnerColonyId: "c", SnapshotId: "s", MapId: "m", WorldObjectId: "w", Tile: 901, Label: "é", CanRaid: true }
                && target.TileLayerId == Math.Max(0, layer), "Transporter target mapping changed.");
        }
        var empty = ClientRaidCommands.TransporterTarget(null, null, null, null, null, 0, 0, null);
        Check(new[] { empty.OwnerUserId, empty.OwnerColonyId, empty.SnapshotId, empty.MapId, empty.WorldObjectId, empty.Label }.All(v => v == ""), "Null target fields were not normalized.");
    }
    private sealed class Fixture : IClientRaidCommandPresentation
    {
        internal readonly ClientRaidLaunchTestPorts Core = new() { Fault = "prepare" };
        internal readonly ClientRaidCommands Commands;
        internal readonly IClientRaidCommandSource Source;
        internal object World = new(); internal bool Available = true; internal int PawnCount = 1, Confirmations;
        internal IReadOnlyList<ClientRaidMenuOption>? Options;
        internal Func<Task>? Confirm;
        internal string? Prompt;
        internal Action<string>? Hook;
        internal readonly List<string> Statuses = new(), Messages = new();
        internal Fixture() { Core.Target.Label = "original-label"; Source = new Bound(this, Core.Ports.GameOwner, World); Commands = new(Core.Session, Core.Launch, this); }
        internal void Open() => Commands.OpenCaravanMenu(Source, new[] { Core.Target });
        public string TargetLabel(ModWorldMapMarkerDto target) { Hook?.Invoke("label"); return target.Label!; }
        public string Text(string key, params (string Name, object? Value)[] args)
        {
            Hook?.Invoke(key.EndsWith("CaravanTargetOption", StringComparison.Ordinal) ? "option-text" : key.EndsWith("ConfirmStart", StringComparison.Ordinal) ? "confirmation-text" : "refusal-text");
            return key + " " + string.Join(" ", args.Select(a => a.Value));
        }
        public void Status(string message) => Statuses.Add(message);
        public void Message(string message, bool rejected) { Check(rejected, "Command emitted a success message."); Messages.Add(message); }
        public void Menu(IReadOnlyList<ClientRaidMenuOption> options) { Options = options; Hook?.Invoke("menu"); }
        void IClientRaidCommandPresentation.Confirm(string message, Func<Task> confirmed)
        { Confirmations++; Prompt = message; Confirm = confirmed; Hook?.Invoke("confirmation"); }
        private sealed record Bound(Fixture F, object Game, object World) : IClientRaidCommandSource
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Core.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public bool IsAvailable => F.Available;
            public int PawnCount => F.PawnCount;
            public IClientRaidLaunchRuntime CreateLaunch() { F.Hook?.Invoke("launch"); return F.Core.BindLaunch(); }
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
