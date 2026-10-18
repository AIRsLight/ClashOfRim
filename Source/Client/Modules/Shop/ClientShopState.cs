using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Shop;

internal sealed class ClientShopState
{
    private readonly ClientGameRequestTracker requests;
    private readonly IClientShopPresentation presentation;
    private List<ModServerShopListingDto> listings = new();
    private string? message;
    private int version;
    internal ClientShopState(ClientSessionLifecycle session, IClientSnapshotGame game, IClientShopPresentation presentation)
    {
        this.presentation = presentation;
        requests = new(session, game, () => { listings.Clear(); message = null; version++; });
    }
    internal bool Busy => requests.Busy;
    internal string Message { get => requests.Read(() => message ?? presentation.Text("StatusIdle")); set => requests.Read(() => message = value); }
    internal int Version => requests.Read(() => version);
    internal List<ModServerShopListingDto> CopyListings() => requests.Read(() => Copy(listings));
    internal ClientGameRequestTracker.Scope Capture() => requests.Capture();
    internal void Reset() => requests.Reset();
    internal ClientGameRequestTracker.Request? Begin(ClientSynchronizationCoordinator.Lease? admission = null) => requests.Begin(admission);
    internal void Track(ClientGameRequestTracker.Request request, ClientSynchronizationCoordinator.Lease admission) => requests.Track(request, admission);
    internal bool TryApply(ClientGameRequestTracker.Scope scope, Action action) => requests.TryApply(scope, action);
    internal bool TryApply(ClientGameRequestTracker.Request request, Action action) => requests.TryApply(request, action);
    internal void End(ClientGameRequestTracker.Request request) => requests.End(request);

    // Mutations run inside the owning request publication. Cache and caller never
    // share mutable package DTOs, including non-wire preview bytes.
    internal void Replace(IReadOnlyList<ModServerShopListingDto> incoming) { listings = Copy(incoming); version++; }
    internal void Upsert(ModServerShopListingDto listing)
    {
        var copy = Copy(new[] { listing })[0];
        int index = listings.FindIndex(old => old.ListingId == listing.ListingId);
        if (index < 0) listings.Add(copy); else listings[index] = copy;
        version++;
    }
    internal void Remove(string id) { if (listings.RemoveAll(listing => listing.ListingId == id) > 0) version++; }
    internal static List<ModServerShopListingDto> Copy(IReadOnlyList<ModServerShopListingDto> source)
    {
        var copy = ClientProtocolJson.Deserialize<List<ModServerShopListingDto>>(ClientProtocolJson.Serialize(source.ToList()), static type => type);
        for (int i = 0; i < source.Count; i++)
            if (source[i].Item is { } item && copy[i].Item is { } copied)
                ClientPackageCopies.CopyPreviewSidecars(new[] { item }, new[] { copied });
        return copy;
    }
}
