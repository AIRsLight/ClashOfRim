using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Multiplayer;

internal static class MultiplayerPlayerPanelTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await ListsAndRefresh();
                await PreparedCommands();
            }
            await Ownership();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static ClientPlayerPanel Panel(PlayerDirectoryModuleTests.Fixture f) => new(f.Session, f.Ports, f.Module, f.Actions, f.Sync);
    private static ModPlayerSummaryDto Player(string user, string colony, int? wealth = 100, bool online = true) =>
        new() { UserId = user, ColonyId = colony, CurrentSnapshotId = "snapshot", DisplayName = "same", LatestSnapshotWealth = wealth, Online = online };

    private static async Task ListsAndRefresh()
    {
        var f = new PlayerDirectoryModuleTests.Fixture(); var panel = Panel(f);
        f.Reply.Players = new()
        {
            Player("İ", "3"), Player("i", "2"), Player("I", "1"), Player("alice", "colony", 10, false),
            Player("offline", "home", 0, false), Player("unknown", "home", null, false),
            Player("no-colony", ""), new() { UserId = "no-snapshot", ColonyId = "home" }
        };
        Check(f.Sync.TryBeginManual(out var busy), "Cannot occupy refresh admission.");
        await panel.RefreshAsync(100f);
        Check(f.Calls == 0, "Busy panel started refresh."); busy!.Dispose();
        await f.Ports.PumpUntil(panel.RefreshAsync(100f));
        var view = panel.Read("unknown-name");
        Check(view.Players.Select(player => player.UserId).SequenceEqual(new[] { "alice", "I", "i", "İ", "offline", "unknown" }),
            "Player filtering, self/online priority or ordinal ordering changed.");
        Check(view.WealthRanked.Select(player => player.UserId).SequenceEqual(new[] { "I", "i", "İ", "alice", "offline", "unknown" }),
            "Known zero wealth, unknown wealth or tied ranking changed.");
        Check(view.IsOnline(view.Players[0]) && !view.IsSelf(Player("alice", "other")), "Self is no longer identified by both user and colony.");
        Check(ReferenceEquals(view, panel.Read("unknown-name")), "Unchanged player view was rebuilt.");
        view.Players[0].DisplayName = "edited view";
        Check(f.Module.CopyPlayers().Single(player => player.UserId == "alice").DisplayName == "same", "Presentation cache mutated the directory.");
        Check(!ReferenceEquals(view, panel.Read("different-language")), "Localized unknown label did not invalidate ranking input.");
        await panel.RefreshAsync(129.9f); Check(f.Calls == 1, "Player refresh ignored its 30-second interval.");
        await f.Ports.PumpUntil(panel.RefreshAsync(130f)); Check(f.Calls == 2, "Player refresh did not resume at its deadline.");
        panel.RequestRefresh();
        await f.Ports.PumpUntil(panel.RefreshAsync(130f)); Check(f.Calls == 3, "Reentering a page did not request a fresh player list.");
        var current = panel.Read("unknown-name"); var owner = panel.Ownership;
        f.Ports.Snapshot = "advanced";
        Check(ReferenceEquals(owner, panel.Ownership) && ReferenceEquals(current, panel.Read("unknown-name")), "Snapshot advancement discarded page state.");
        panel.Close();
        Check(!ReferenceEquals(owner, panel.Ownership) && panel.Read("unknown-name").Players.Count == 6,
            "Closing a page retained its ownership or cleared business data.");
        await f.Ports.PumpUntil(panel.RefreshAsync(130f)); Check(f.Calls == 4, "Reopened page retained its old timer.");
        Check(ClientPlayerPanel.DisplayName(new() { DisplayName = "文化", UserId = "u" }, "?") == "文化"
            && ClientPlayerPanel.DisplayName(new() { UserId = "u" }, "?") == "u"
            && ClientPlayerPanel.DisplayName(new(), "?") == "?", "Player name fallbacks changed.");
    }

    private static async Task PreparedCommands()
    {
        foreach (string kind in new[] { "AllianceRequest", "AllianceCancellation", "WarDeclaration", "PeaceRequest", "SupportRequest" })
        {
            var f = new PlayerDirectoryModuleTests.Fixture(); var panel = Panel(f);
            var target = Player("bob", "target"); target.DisplayName = "文化";
            var send = panel.PrepareDiplomacy(kind, target);
            target.UserId = "edited"; target.ColonyId = "edited"; target.CurrentSnapshotId = "edited"; target.DisplayName = "edited";
            await f.Ports.PumpUntil(send("custom message"));
            Check(f.Creations == 1 && f.Command?.Target == new ClientDiplomacyTarget("bob", "target", "文化")
                && f.Command.Message == "custom message" && f.Command.Kind == kind && f.Module.Selected.SnapshotId == "snapshot",
                "Prepared diplomacy command borrowed later target edits or changed its message/selection.");
        }
    }

    private static async Task Ownership()
    {
        foreach (string change in new[] { "user", "auth", "server", "colony", "game", "login", "generation", "close" })
        {
            var f = new PlayerDirectoryModuleTests.Fixture(); var panel = Panel(f);
            await f.Ports.PumpUntil(panel.RefreshAsync(100f));
            var owner = panel.Ownership; var old = panel.Read("?");
            var send = panel.PrepareDiplomacy("SupportRequest", Player("bob", "target"));
            if (change == "close") panel.Close(); else f.Change(change);
            await send("stale");
            Check(f.Creations == 0 && !ReferenceEquals(owner, panel.Ownership) && !ReferenceEquals(old, panel.Read("?")),
                "Old page/dialog survived ownership change: " + change);
            if (change != "close") Check(panel.Read("?").Players.Count == 0, "Previous session's players remained visible: " + change);
            await f.Ports.PumpUntil(panel.RefreshAsync(100f));
            Check(f.Calls == 2, "Previous ownership's refresh timer survived: " + change);
        }
        var live = new PlayerDirectoryModuleTests.Fixture(); var current = Panel(live);
        var stillValid = current.PrepareDiplomacy("SupportRequest", Player("bob", "target"));
        live.Ports.Snapshot = "advanced";
        await live.Ports.PumpUntil(stillValid(null));
        Check(live.Creations == 1, "Snapshot advancement invalidated a current page command.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
