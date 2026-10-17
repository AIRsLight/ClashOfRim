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
    private readonly ClientSessionLifecycle session;
    private readonly IClientSnapshotGame game;
    private readonly IClientShopPresentation presentation;
    private Scope? owner;
    private Request? work;
    private List<ModServerShopListingDto> listings = new();
    private string message = "";
    private int version;

    internal ClientShopState(ClientSessionLifecycle session, IClientSnapshotGame game, IClientShopPresentation presentation)
    { this.session = session; this.game = game; this.presentation = presentation; }

    internal bool Busy => Read(() => work is not null);
    internal string Message { get => Read(() => message); set => Read(() => message = value); }
    internal int Version => Read(() => version);
    internal List<ModServerShopListingDto> CopyListings() => Read(() => Copy(listings));
    internal Scope Capture() => Read(() => owner!);
    internal void Reset() { using var p = ClientSessionWork.EnterPublication(); owner = null; Normalize(); }

    private T Read<T>(Func<T> read)
    { using var p = ClientSessionWork.EnterPublication(); Normalize(); return read(); }

    private void Normalize()
    {
        if (owner is not null && Owns(owner)) return;
        work?.Admission?.Dispose();
        owner = new(session.CaptureOwnership(), session.ReadContext(), game.BindCurrentGame());
        work = null; listings.Clear(); message = presentation.Text("StatusIdle"); version++;
    }
    private bool Owns(Scope candidate) => candidate.Game.IsCurrent && session.Owns(candidate.Owner, candidate.Context, false);
    internal bool TryApply(Scope candidate, Action action)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() =>
        {
            Normalize();
            if (!ReferenceEquals(owner, candidate) || !Owns(candidate)) return;
            action(); applied = true;
        });
        return applied;
    }

    // A request owns its busy marker. Reads/admin commands do not reserve the global
    // snapshot lease; their catalogue is independent of the current save's revision.
    internal Request? Begin(ClientSynchronizationCoordinator.Lease? admission = null) =>
        Read(() => work is null ? work = new(owner!, session.ReadContext()) { Admission = admission } : null);
    internal void Track(Request request, ClientSynchronizationCoordinator.Lease admission) => TryApply(request, () => request.Admission = admission);
    internal bool TryApply(Request request, Action action)
    {
        bool applied = false;
        TryApply(request.Scope, () =>
        {
            if (!ReferenceEquals(work, request)) return;
            action(); applied = true;
        });
        return applied;
    }
    internal void End(Request request) => TryApply(request, () => { request.Admission?.Dispose(); work = null; });

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

    internal sealed record Scope(ClientSessionLifecycle.Ownership Owner, ClashOfRimClientNetworkContext Context, IClientSnapshotGameSession Game);
    internal sealed record Request(Scope Scope, ClashOfRimClientNetworkContext Context)
    { internal ClientSynchronizationCoordinator.Lease? Admission { get; set; } }
}
