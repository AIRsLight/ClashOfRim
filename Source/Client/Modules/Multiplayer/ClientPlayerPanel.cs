using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

// Shared player presentation state. The directory remains the sole owner of
// server data; diplomacy and achievements share this detached, sorted view.
internal sealed class ClientPlayerPanel
{
    private readonly ClientPlayerDirectory directory;
    private readonly ClientDiplomacyActions actions;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientGameRequestTracker lifetime;
    private ClientPlayerPanelView? cached;
    private float nextRefreshAt;

    internal ClientPlayerPanel(ClientSessionLifecycle session, IClientSnapshotGame game,
        ClientPlayerDirectory directory, ClientDiplomacyActions actions, ClientSynchronizationCoordinator synchronization)
    {
        this.directory = directory; this.actions = actions; this.synchronization = synchronization;
        lifetime = new(session, game, () => { cached = null; nextRefreshAt = 0f; });
    }

    internal object Ownership => lifetime.Capture();
    internal bool Busy => synchronization.ManualInProgress;
    internal void Close() => lifetime.Reset();
    internal void RequestRefresh() => lifetime.Read(() => nextRefreshAt = 0f);
    internal Task RefreshAsync(float now) => lifetime.Read(() =>
    {
        if (Busy || now < nextRefreshAt) return Task.CompletedTask;
        nextRefreshAt = now + 30f;
        return directory.RefreshAsync(manual: true);
    });

    internal ClientPlayerPanelView Read(string unknownPlayerName) => lifetime.Read(() =>
    {
        int version = directory.Version;
        if (cached is not null && cached.Version == version && cached.UnknownPlayerName == unknownPlayerName) return cached;
        var context = lifetime.Capture().Context;
        var snapshot = directory.CopyPlayers()
            .Where(player => !string.IsNullOrWhiteSpace(player.ColonyId) && !string.IsNullOrWhiteSpace(player.CurrentSnapshotId))
            .ToList();
        bool IsSelf(ModPlayerSummaryDto player) => player.UserId == context.UserId && player.ColonyId == context.ColonyId;
        var players = snapshot.OrderBy(player => IsSelf(player) ? 0 : 1)
            .ThenByDescending(player => IsSelf(player) || player.Online)
            .ThenBy(player => player.UserId, StringComparer.Ordinal)
            .ThenBy(player => player.ColonyId, StringComparer.Ordinal).ToList().AsReadOnly();
        var ranked = snapshot.OrderByDescending(player => player.LatestSnapshotWealth.HasValue)
            .ThenByDescending(player => player.LatestSnapshotWealth ?? 0)
            .ThenBy(player => DisplayName(player, unknownPlayerName), StringComparer.Ordinal)
            .ThenBy(player => player.ColonyId, StringComparer.Ordinal).ToList().AsReadOnly();
        return cached = new(context.UserId, context.ColonyId, version, unknownPlayerName, players, ranked);
    });

    internal Func<string?, Task> PrepareDiplomacy(string kind, ModPlayerSummaryDto target)
    {
        var owner = lifetime.Capture();
        var captured = new ModPlayerSummaryDto
        { UserId = target.UserId, ColonyId = target.ColonyId, DisplayName = target.DisplayName, CurrentSnapshotId = target.CurrentSnapshotId };
        return message =>
        {
            Task work = Task.CompletedTask;
            lifetime.TryApply(owner, () => actions.Create(kind, captured, message, out work));
            return work;
        };
    }

    internal static string DisplayName(ModPlayerSummaryDto player, string unknownPlayerName) =>
        !string.IsNullOrWhiteSpace(player.DisplayName) ? player.DisplayName!
        : string.IsNullOrWhiteSpace(player.UserId) ? unknownPlayerName : player.UserId;
}

internal sealed record ClientPlayerPanelView(string UserId, string ColonyId, int Version, string UnknownPlayerName,
    IReadOnlyList<ModPlayerSummaryDto> Players, IReadOnlyList<ModPlayerSummaryDto> WealthRanked)
{
    internal bool IsSelf(ModPlayerSummaryDto player) => player.UserId == UserId && player.ColonyId == ColonyId;
    internal bool IsOnline(ModPlayerSummaryDto player) => IsSelf(player) || player.Online;
}
