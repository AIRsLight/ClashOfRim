using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

// The catalog owns server data. Player and administrator pages share this
// detached projection; choosing a mode does not rebuild a large catalog.
internal sealed class ClientShopPanel
{
    private const int BatchCount = 1;
    private readonly ClientShopState state;
    private readonly ClientShopCatalog catalog;
    private readonly ClientShopOperations operations;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientGameRequestTracker lifetime;
    private readonly PanelRefreshSchedule refresh = new();
    private ClientShopPanelView? cached;
    private Selection selection = new(ServerShopListingKinds.SellToPlayer);

    internal ClientShopPanel(ClientSessionLifecycle session, IClientSnapshotGame game, ClientShopState state,
        ClientShopCatalog catalog, ClientShopOperations operations, ClientSynchronizationCoordinator synchronization)
    {
        this.state = state; this.catalog = catalog; this.operations = operations; this.synchronization = synchronization;
        lifetime = new(session, game, () =>
        { cached = null; selection = new(ServerShopListingKinds.SellToPlayer); refresh.Request(); });
    }

    internal object Ownership => lifetime.Capture();
    internal string Mode => lifetime.Read(() => selection.Kind);
    internal string Status => state.Message;
    internal bool CanRefresh => !state.Busy;
    internal void Close() => lifetime.Reset();
    internal void RequestRefresh() => lifetime.Read(() => { refresh.Request(); return true; });
    internal Task RefreshAsync(float now, bool manual = false) => lifetime.Read(() =>
    {
        if (!refresh.TryStart(now, state.Busy || (!manual && synchronization.ManualInProgress), manual)) return Task.CompletedTask;
        return catalog.RefreshAsync();
    });
    internal bool SelectMode(string kind) => lifetime.Read(() =>
    {
        string normalized = ClientShopRules.NormalizeListingKind(kind);
        if (selection.Kind == normalized) return false;
        selection = new(normalized);
        return true;
    });

    internal ClientShopPanelView Read() => lifetime.Read(() =>
    {
        int version = state.Version;
        if (cached?.Version == version) return cached;
        var all = state.CopyListings();
        var sell = new List<ModServerShopListingDto>();
        var buy = new List<ModServerShopListingDto>();
        foreach (var listing in all) (ClientShopRules.IsBuyOrder(listing) ? buy : sell).Add(listing);
        return cached = new(version, all.AsReadOnly(), sell.AsReadOnly(), buy.AsReadOnly());
    });

    internal ClientShopPanelQuote Quote(ModServerShopListingDto listing) => lifetime.Read(() =>
    {
        bool available = listing.StockCount >= BatchCount;
        return new ClientShopPanelQuote(ClientShopRules.IsBuyOrder(listing), BatchCount, ClientShopRules.TotalPrice(listing, BatchCount),
            available, listing.Item is not null && available && !state.Busy && !synchronization.ManualInProgress);
    });

    internal Action PreparePurchase(ModServerShopListingDto listing)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var owner = lifetime.Capture();
        var mode = selection;
        // Business binding freezes the quote and keeps market reset invalidation.
        var purchase = operations.BindPurchase(listing, BatchCount);
        return () => lifetime.TryApply(owner, () =>
        {
            if (ReferenceEquals(mode, selection)) purchase();
        });
    }

    private sealed record Selection(string Kind);
}

internal sealed record ClientShopPanelView(int Version, IReadOnlyList<ModServerShopListingDto> All,
    IReadOnlyList<ModServerShopListingDto> SellToPlayer, IReadOnlyList<ModServerShopListingDto> BuyFromPlayer);
internal sealed record ClientShopPanelQuote(bool BuyFromPlayer, int Count, int TotalPrice, bool Available, bool CanPurchase);
