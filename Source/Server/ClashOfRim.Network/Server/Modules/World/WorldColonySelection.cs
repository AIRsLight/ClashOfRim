using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed record WorldColonySelectionResult(string ColonyId, LatestSnapshotRecord? Snapshot, bool HasRegisteredSite)
{
    internal bool HasExistingColony => Snapshot is not null || HasRegisteredSite;
}

internal sealed class WorldColonySelection(PlayerRegistry players, IColonySnapshotIndexStore snapshots, IAuthoritativeEventLedger ledger)
{
    internal WorldColonySelectionResult Resolve(string userId, string? requested, WorldConfigurationDto? configuration, DateTimeOffset nowUtc)
    {
        string requestedColonyId = string.IsNullOrWhiteSpace(requested) ? DefaultColonyId(userId) : requested.Trim();
        var sites = configuration is null ? Array.Empty<PlayerColonySiteDto>() : WorldColonySites.Current(configuration);
        var selected = Read(userId, players.ResolveActiveColonyId(userId, requestedColonyId, nowUtc), sites);
        if (!selected.HasExistingColony)
        {
            string? existing = FindExisting(userId, requestedColonyId, sites);
            if (!string.IsNullOrWhiteSpace(existing) && existing != selected.ColonyId) selected = Read(userId, existing, sites);
        }
        if (selected.ColonyId == requestedColonyId && !selected.HasExistingColony && HasHistoricalReference(userId, requestedColonyId))
        {
            players.MarkDeleted(userId, requestedColonyId, null, nowUtc);
            selected = Read(userId, players.ResolveActiveColonyId(userId, requestedColonyId, nowUtc), sites);
        }
        return selected;
    }

    private WorldColonySelectionResult Read(string userId, string colonyId, IReadOnlyList<PlayerColonySiteDto> sites)
        => new(colonyId, snapshots.GetLatest(userId, colonyId), sites.Any(site => site.UserId == userId && site.ColonyId == colonyId && site.Tile >= 0));

    private string? FindExisting(string userId, string requestedColonyId, IReadOnlyList<PlayerColonySiteDto> sites)
    {
        var eligible = snapshots.ListLatest().Where(snapshot => snapshot.Identity.OwnerId == userId
            && !string.IsNullOrWhiteSpace(snapshot.Identity.ColonyId) && !players.IsDeleted(userId, snapshot.Identity.ColonyId!)).ToArray();
        var latest = LatestSnapshotLookup.Build(eligible).ForUser(userId);
        if (latest is not null) return latest.Identity.ColonyId;
        var colonies = sites.Where(site => site.UserId == userId && !string.IsNullOrWhiteSpace(site.ColonyId)
            && site.Tile >= 0 && !players.IsDeleted(userId, site.ColonyId)).Select(site => site.ColonyId);
        return colonies.Contains(requestedColonyId, StringComparer.Ordinal) ? requestedColonyId
            : colonies.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
    }

    private bool HasHistoricalReference(string userId, string colonyId)
        => ledger.ListByTypeForActor(ServerEventType.ServerNotification, userId, colonyId).Any(item =>
            EventVisibility.MatchesParty(item.Actor, userId, colonyId) && ColonyTombstones.IsFor(item, userId, colonyId));

    private static string DefaultColonyId(string userId)
    {
        string normalized = new(userId.Trim().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        return "colony-" + (string.IsNullOrWhiteSpace(normalized) ? Guid.NewGuid().ToString("N")[..8] : normalized);
    }
}
