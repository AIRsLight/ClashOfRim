using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientOccupiedColonySitesRuntime { IClientOccupiedColonySitesSession Bind(); }
internal interface IClientOccupiedColonySitesSession
{
    bool IsCurrent { get; }
    bool CanReserve(int tile, int layer);
    bool IsNeighborOrSame(int tile, int other, int layer);
    void EnsureProxy(string user);
}

// Reservations survive vanilla replacing Game during world creation. Their data
// belongs to the account and server world; geometry and effects bind the live world.
internal sealed class ClientOccupiedColonySites
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientRequestTracker updates;
    private readonly ClientRequestTracker<string> cache;
    private readonly IClientWorldIdentity identity;
    private readonly IClientOccupiedColonySitesRuntime runtime;
    private readonly ClientWorldConfiguration configuration;
    private Dictionary<(int Tile, int Layer), ModPlayerColonySiteDto> sites = new();
    internal event Action<ModColonyAppearanceDto?>? OwnAppearanceObserved;

    internal ClientOccupiedColonySites(ClientSessionLifecycle session, IClientWorldIdentity identity,
        IClientOccupiedColonySitesRuntime runtime, ClientWorldConfiguration configuration)
    {
        this.session = session; this.identity = identity; this.runtime = runtime; this.configuration = configuration;
        updates = new(session, static () => { });
        cache = new(session, () => identity.WorldId, world => world == identity.WorldId, () => sites = new());
    }

    internal void Reset()
    {
        using var publication = ClientSessionWork.EnterPublication();
        updates.Reset(); cache.Reset();
    }

    internal void Replace(ModWorldConfigurationDto? source, Action<ModColonyAppearanceDto?>? observe = null, Func<bool>? current = null, bool remember = true)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var request = updates.Begin(replace: true)!;
        var scope = cache.Capture(); var bound = runtime.Bind();
        bool Current() => current?.Invoke() != false && !session.IsUnavailable && bound.IsCurrent
            && updates.TryApply(request, static () => { }) && cache.TryApply(scope, static () => { });
        try
        {
            string? worldId = source?.WorldConfigurationId;
            var incoming = ClientProtocolJson.Copy(source?.PlayerColonySites?.ToArray() ?? Array.Empty<ModPlayerColonySiteDto>())
                .Where(site => site.Tile >= 0).ToArray();
            if (!Current()) return;
            string expectedWorld = string.IsNullOrWhiteSpace(worldId) ? scope.Boundary : worldId!.Trim();
            if (remember) configuration.Remember(new ModWorldConfigurationDto { WorldConfigurationId = worldId ?? string.Empty });
            // Remember may persist a new world ID or invoke reentrant application code.
            if (current?.Invoke() == false || session.IsUnavailable || !bound.IsCurrent || !updates.TryApply(request, static () => { }) || identity.WorldId != expectedWorld) return;
            scope = cache.Capture();
            var replacement = new Dictionary<(int, int), ModPlayerColonySiteDto>();
            foreach (var site in incoming) replacement[(site.Tile, site.TileLayerId)] = site;
            sites = replacement;

            foreach (var site in incoming)
            {
                if (site.UserId == request.Context.UserId && site.ColonyId == request.Context.ColonyId)
                {
                    if (!Current()) return;
                    observe?.Invoke(site.Appearance is null ? null : ClientProtocolJson.Copy(site.Appearance));
                    foreach (Action<ModColonyAppearanceDto?> handler in OwnAppearanceObserved?.GetInvocationList() ?? Array.Empty<Delegate>())
                    {
                        if (!Current()) return;
                        handler(site.Appearance is null ? null : ClientProtocolJson.Copy(site.Appearance));
                    }
                }
            }
            var owners = incoming.Select(site => site.UserId)
                .Where(user => !string.IsNullOrWhiteSpace(user) && user != request.Context.UserId).Distinct(StringComparer.Ordinal);
            foreach (string owner in owners)
            {
                if (!Current()) return;
                bound.EnsureProxy(owner);
            }
        }
        finally { updates.End(request); }
    }

    internal void Merge(IEnumerable<ModWorldMapMarkerDto> markers)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = cache.Capture(); var view = sites; var bound = runtime.Bind();
        var incoming = markers.Where(marker => marker.Kind == "TradeableColony" && marker.Tile >= 0 && !string.IsNullOrWhiteSpace(marker.OwnerUserId))
            .Select(marker => new ModPlayerColonySiteDto
            {
                UserId = marker.OwnerUserId, ColonyId = marker.OwnerColonyId ?? string.Empty,
                WorldObjectId = marker.WorldObjectId, MapUniqueId = marker.MapId,
                Tile = marker.Tile, TileLayerId = marker.TileLayerId, Label = marker.Label, Appearance = marker.Appearance
            }).ToArray();
        incoming = ClientProtocolJson.Copy(incoming);
        if (incoming.Length == 0 || session.IsUnavailable || !bound.IsCurrent
            || !cache.TryApply(scope, static () => { }) || !ReferenceEquals(sites, view)) return;
        // Only a real delta supersedes an update's pending effects. Enumeration
        // cannot replace a newer view published reentrantly in the same world.
        var request = updates.Begin(replace: true)!;
        try
        {
            var replacement = new Dictionary<(int, int), ModPlayerColonySiteDto>(sites);
            foreach (var site in incoming) replacement[(site.Tile, site.TileLayerId)] = site;
            sites = replacement;
        }
        finally { updates.End(request); }
    }

    internal bool IsBlocked(int tile, int layer, out ModPlayerColonySiteDto? blockingSite)
    {
        using var publication = ClientSessionWork.EnterPublication();
        blockingSite = null;
        var scope = cache.Capture(); var view = sites; var bound = runtime.Bind();
        bool Current() => !session.IsUnavailable && bound.IsCurrent && cache.TryApply(scope, static () => { }) && ReferenceEquals(sites, view);
        if (view.Count == 0 || !Current() || !bound.CanReserve(tile, layer)) return false;
        foreach (var site in view.Values)
        {
            if (!Current()) return false;
            if (site.TileLayerId != layer || !bound.CanReserve(site.Tile, layer)) continue;
            if (!Current()) return false;
            if (!bound.IsNeighborOrSame(tile, site.Tile, layer)) continue;
            if (!Current()) return false;
            blockingSite = ClientProtocolJson.Copy(site);
            return true;
        }
        return false;
    }
}
