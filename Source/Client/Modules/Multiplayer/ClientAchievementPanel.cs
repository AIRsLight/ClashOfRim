using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Achievements;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal interface IClientAchievementTrophyPlacement
{
    bool TryCreate(ModAchievementSummaryDto achievement, ClientAchievementTarget owner, string ownerDisplayName);
}

internal sealed class ClientAchievementPanel
{
    private readonly ClientAchievementQueries queries;
    private readonly ClientPlayerPanel players;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly IClientAchievementTrophyPlacement trophies;
    private readonly ClientGameRequestTracker lifetime;
    private readonly PanelRefreshSchedule refresh = new();
    private ClientAchievementTarget? selected;
    private ClientAchievementPanelView? cached;

    internal ClientAchievementPanel(ClientSessionLifecycle session, IClientSnapshotGame game, ClientAchievementQueries queries,
        ClientPlayerPanel players, ClientSynchronizationCoordinator synchronization, IClientAchievementTrophyPlacement trophies)
    {
        this.queries = queries; this.players = players; this.synchronization = synchronization; this.trophies = trophies;
        lifetime = new(session, game, () => { selected = null; cached = null; refresh.Request(); });
    }

    internal object Ownership => lifetime.Capture();
    internal bool Busy => synchronization.ManualInProgress;
    internal string Status => queries.Message;
    internal void Close() => lifetime.Reset();
    internal void RequestRefresh() => lifetime.Read(() => { refresh.Request(); return true; });
    private ClientAchievementTarget Self
    {
        get { var context = lifetime.Capture().Context; return new(context.UserId, context.ColonyId); }
    }
    private ClientAchievementTarget Target => selected ??= Self;

    internal Task RefreshAsync(float now, bool force = false) => lifetime.Read(() =>
        refresh.TryStart(now, Busy, force) ? queries.RefreshAsync(Target.UserId, Target.ColonyId) : Task.CompletedTask);

    internal IReadOnlyList<ModPlayerSummaryDto> SelectablePlayers(string unknownPlayerName) => lifetime.Read(() =>
    {
        var view = players.Read(unknownPlayerName);
        var selectable = view.Players.Where(player => !string.IsNullOrWhiteSpace(player.UserId) && !string.IsNullOrWhiteSpace(player.ColonyId)).ToList();
        if (!selectable.Any(view.IsSelf) && !string.IsNullOrWhiteSpace(view.UserId) && !string.IsNullOrWhiteSpace(view.ColonyId))
            selectable.Insert(0, new() { UserId = view.UserId, ColonyId = view.ColonyId, DisplayName = view.UserId, Online = true });
        return selectable.AsReadOnly();
    });

    internal string TargetDisplayName(IReadOnlyList<ModPlayerSummaryDto> selectable, string unknownPlayerName, string selectPlayerLabel) => lifetime.Read(() =>
    {
        var player = selectable.FirstOrDefault(player => player.UserId == Target.UserId && player.ColonyId == Target.ColonyId);
        return player is not null ? ClientPlayerPanel.DisplayName(player, unknownPlayerName)
            : Target == Self ? Self.UserId : string.IsNullOrWhiteSpace(Target.UserId) ? selectPlayerLabel : Target.UserId;
    });

    internal Func<float, Task> PrepareSelection(ModPlayerSummaryDto player)
    {
        var owner = lifetime.Capture();
        var target = new ClientAchievementTarget(player.UserId, player.ColonyId);
        return now =>
        {
            Task work = Task.CompletedTask;
            lifetime.TryApply(owner, () =>
            {
                if (Target == target) return;
                selected = target; cached = null; refresh.Started(now);
                work = queries.RefreshAsync(target.UserId, target.ColonyId);
            });
            return work;
        };
    }

    internal ClientAchievementPanelView Read() => lifetime.Read(() =>
    {
        int version = queries.Version;
        if (cached is not null && cached.Version == version && cached.Target == Target) return cached;
        IReadOnlyList<ModAchievementSummaryDto> items = queries.Target == Target
            ? queries.CopyAchievements().Where(achievement => achievement.Value != 0)
                .OrderBy(achievement => achievement.Category, StringComparer.Ordinal)
                .ThenBy(achievement => achievement.AchievementId, StringComparer.Ordinal).ToList().AsReadOnly()
            : Array.Empty<ModAchievementSummaryDto>();
        return cached = new(Target, Target == Self, version, items);
    });

    internal Func<bool> PrepareTrophy(string achievementId, string ownerDisplayName)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var owner = lifetime.Capture();
        var target = Target;
        return () =>
        {
            bool created = false;
            lifetime.TryApply(owner, () =>
            {
                if (!ReferenceEquals(Target, target) || Target != Self || queries.Target != Target) return;
                // Resolve against the current business snapshot, never trust a
                // stale or edited row from a page's detached display cache.
                var achievement = queries.CopyAchievements().FirstOrDefault(item => item.Value != 0 && item.AchievementId == achievementId);
                if (achievement is not null) created = trophies.TryCreate(achievement, Target, ownerDisplayName);
            });
            return created;
        };
    }
}

internal sealed record ClientAchievementPanelView(ClientAchievementTarget Target, bool IsSelf, int Version,
    IReadOnlyList<ModAchievementSummaryDto> Achievements);
