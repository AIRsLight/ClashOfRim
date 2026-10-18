using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Achievements;
using AIRsLight.ClashOfRim.Modules.Multiplayer;

internal static class MultiplayerAchievementPanelTests
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
                await ViewsAndRefresh(); await Selection(); await Trophies();
            }
            await Ownership();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static async Task ViewsAndRefresh()
    {
        var f = new Fixture();
        var empty = f.Panel.Read();
        Check(empty.Target == new ClientAchievementTarget("alice", "colony") && empty.IsSelf && empty.Achievements.Count == 0,
            "Initial page did not select its current owner.");
        await f.Directory.Ports.PumpUntil(f.Directory.Module.RefreshAsync(false));
        var choices = f.Panel.SelectablePlayers("unknown");
        Check(choices.Count == 2 && choices[0].UserId == "alice" && choices[1].UserId == "bob", "Missing own colony was not inserted in shared player choices.");
        Check(f.Panel.TargetDisplayName(choices, "unknown", "select") == "alice", "Initial target name changed.");
        f.Directory.Reply.Players[0].CurrentSnapshotId = "own";
        await f.Directory.Ports.PumpUntil(f.Directory.Module.RefreshAsync(false));
        Check(f.Panel.SelectablePlayers("unknown").Count(player => player.UserId == "alice") == 1, "Own colony was inserted twice.");

        Check(f.Directory.Sync.TryBeginManual(out var busy), "Cannot occupy manual operation.");
        await f.Panel.RefreshAsync(10f); await f.Panel.RefreshAsync(10f, force: true);
        Check(f.Queries.Calls == 0, "Busy page started a periodic or manual refresh."); busy!.Dispose();
        await f.Panel.RefreshAsync(10f);
        var view = f.Panel.Read();
        Check(view.Achievements.Select(item => item.AchievementId).SequenceEqual(new[] { "I", "i", "negative", "İ", "tail" }),
            "Category/ID sorting, nonzero filtering or negative achievement semantics changed.");
        Check(ReferenceEquals(view, f.Panel.Read()) && f.Panel.Status == "ClashOfRim.Achievement.StatusRefreshed", "Unchanged cache or status changed.");
        view.Achievements[0].Value = 999;
        Check(f.Queries.Module.CopyAchievements().Single(item => item.AchievementId == "I").Value == 3, "Display cache mutated the query module.");
        await f.Panel.RefreshAsync(39.9f); Check(f.Queries.Calls == 1, "Achievement page refreshed before its deadline.");
        await f.Panel.RefreshAsync(40f); Check(f.Queries.Calls == 2, "Achievement page did not refresh at its deadline.");
        await f.Panel.RefreshAsync(40f, force: true); Check(f.Queries.Calls == 3, "Manual refresh did not bypass the deadline.");
        f.Panel.RequestRefresh(); await f.Panel.RefreshAsync(40f); Check(f.Queries.Calls == 4, "Tab reentry did not reset its deadline.");
        var owner = f.Panel.Ownership; view = f.Panel.Read();
        f.Directory.Ports.Snapshot = "advanced";
        Check(ReferenceEquals(owner, f.Panel.Ownership) && ReferenceEquals(view, f.Panel.Read()), "Snapshot advancement discarded current page state.");
    }

    private static async Task Selection()
    {
        var f = new Fixture(); await f.Panel.RefreshAsync(10f);
        var target = new ModPlayerSummaryDto { UserId = "bob", ColonyId = "target" };
        var select = f.Panel.PrepareSelection(target);
        target.UserId = "edited"; target.ColonyId = "edited";
        var gate = new TaskCompletionSource<ModListAchievementsResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Queries.Send = (_, _) => gate.Task;
        var pending = select(20f); await f.Queries.Next(); var sent = await f.Queries.Next();
        Check(sent.Target == new ClientAchievementTarget("bob", "target") && f.Panel.Read().Target == sent.Target
            && !f.Panel.Read().IsSelf && f.Panel.Read().Achievements.Count == 0, "Target edit or previous owner's results leaked into selected page.");
        Check(f.Panel.TargetDisplayName(new[] { new ModPlayerSummaryDto { UserId = "bob", ColonyId = "target", DisplayName = "玩家 İ" } }, "unknown", "select") == "玩家 İ"
            && f.Panel.TargetDisplayName(Array.Empty<ModPlayerSummaryDto>(), "unknown", "select") == "bob", "Selected target label fallbacks changed.");
        await select(21f); Check(f.Queries.Calls == 2, "Selecting the same target restarted its request.");
        f.Queries.Send = null;
        await f.Panel.PrepareSelection(new() { UserId = "charlie", ColonyId = "second" })(22f);
        gate.SetResult(f.Queries.Reply); await pending;
        Check(f.Panel.Read().Target.UserId == "charlie" && f.Queries.Module.Target.UserId == "charlie"
            && f.Panel.Read().Achievements.Count == 5, "Late selected-target reply replaced the current target's results.");
        Check(f.Directory.Sync.TryBeginManual(out var busy), "Cannot occupy manual operation.");
        await f.Panel.PrepareSelection(new() { UserId = "bob", ColonyId = "target" })(23f);
        Check(f.Panel.Read().Achievements.Count == 5 && busy!.IsCurrent, "Independent selected-target query released manual admission."); busy!.Dispose();

        f.Queries.NetworkFailure = true;
        await f.Panel.PrepareSelection(new() { UserId = "failed", ColonyId = "home" })(24f);
        Check(f.Panel.Read().Achievements.Count == 0 && f.Panel.Status.StartsWith("ClashOfRim.Achievement.StatusRefreshFailed"),
            "Failed target query displayed another target's achievements.");
        f.Queries.NetworkFailure = false;
        f.Panel.Close();
        Check(f.Panel.Read().IsSelf && f.Panel.Read().Achievements.Count == 0, "Closed page retained its foreign target.");
        await f.Panel.RefreshAsync(24f);
        Check(f.Panel.Read().Achievements.Count == 5 && f.Queries.Module.Target.UserId == "alice", "Reopened page retained its timer or failed to refresh its owner.");
    }

    private static async Task Trophies()
    {
        var f = new Fixture();
        Check(!f.Panel.PrepareTrophy("I", "owner")(), "Page created a trophy before any achievement query.");
        await f.Panel.RefreshAsync(1f);
        f.Panel.Read().Achievements[0].Value = 999;
        Check(f.Panel.PrepareTrophy("I", "本人")() && f.Created == 1 && f.Placed?.Value == 3
            && f.Placed.SourceSnapshotId == "source" && f.Owner == new ClientAchievementTarget("alice", "colony") && f.OwnerName == "本人",
            "Trophy did not use the current owned achievement and owner metadata.");
        f.Placed!.Value = 1000;
        Check(f.Queries.Module.CopyAchievements().Single(item => item.AchievementId == "I").Value == 3, "Placement port mutated query state.");
        Check(!f.Panel.PrepareTrophy("zero", "owner")() && !f.Panel.PrepareTrophy("missing", "owner")() && f.Created == 1,
            "Unawarded or missing achievement created a trophy.");
        f.Succeeds = false;
        Check(!f.Panel.PrepareTrophy("negative", "owner")() && f.Created == 2, "Placement failure was reported as success.");
        var stale = f.Panel.PrepareTrophy("I", "owner");
        await f.Panel.PrepareSelection(new() { UserId = "bob", ColonyId = "target" })(2f);
        Check(!stale() && !f.Panel.PrepareTrophy("I", "other")() && f.Created == 2, "Other player's achievement created a local trophy.");
        f.Succeeds = true;
        await f.Panel.PrepareSelection(new() { UserId = "alice", ColonyId = "colony" })(3f);
        Check(!stale() && f.Created == 2, "Returning to the original target resurrected an obsolete trophy command.");
    }

    private static async Task Ownership()
    {
        foreach (string change in new[] { "user", "auth", "server", "colony", "game", "login", "generation", "close" })
        {
            var f = new Fixture(); await f.Panel.RefreshAsync(100f);
            var owner = f.Panel.Ownership;
            var trophy = f.Panel.PrepareTrophy("I", "old owner");
            var menu = f.Panel.PrepareSelection(new() { UserId = "bob", ColonyId = "target" });
            if (change == "close") f.Panel.Close(); else f.Queries.Change(change);
            Check(!ReferenceEquals(owner, f.Panel.Ownership) && f.Panel.Read().IsSelf, "Page retained old selection scope: " + change);
            await f.Panel.RefreshAsync(100f); // Same ID is awarded to the replacement owner too.
            int calls = f.Queries.Calls;
            await menu(101f);
            Check(!trophy() && f.Created == 0 && f.Queries.Calls == calls && f.Panel.Read().IsSelf, "Old menu/trophy operated on replacement page: " + change);
        }
    }

    private sealed class Fixture : IClientAchievementTrophyPlacement
    {
        internal readonly PlayerDirectoryModuleTests.Fixture Directory = new();
        internal readonly ClientAchievementQueryTests.Fixture Queries;
        internal readonly ClientAchievementPanel Panel;
        internal int Created;
        internal bool Succeeds = true;
        internal ModAchievementSummaryDto? Placed;
        internal ClientAchievementTarget? Owner;
        internal string? OwnerName;
        internal Fixture()
        {
            Queries = new(Directory.Ports, Directory.Session);
            var players = new ClientPlayerPanel(Directory.Session, Directory.Ports, Directory.Module, Directory.Actions, Directory.Sync);
            Panel = new(Directory.Session, Directory.Ports, Queries.Module, players, Directory.Sync, this);
            Queries.Reply.OwnAchievements = new() { Item("A", "İ", 5), Item("A", "i", 4), Item("A", "I", 3), Item("A", "negative", -1), Item("B", "tail", long.MaxValue), Item("A", "zero", 0) };
        }
        private static ModAchievementSummaryDto Item(string category, string id, long value) =>
            new() { Category = category, AchievementId = id, Value = value, SourceSnapshotId = "source" };
        public bool TryCreate(ModAchievementSummaryDto achievement, ClientAchievementTarget owner, string ownerDisplayName)
        { Created++; Placed = achievement; Owner = owner; OwnerName = ownerDisplayName; return Succeeds; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
