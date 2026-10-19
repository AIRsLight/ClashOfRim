using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

// Owns administrator drafts and delayed UI intents, not the shared catalog or its requests.
internal sealed class ClientAdminShopPanel
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientAdminAccess access;
    private readonly ClientShopState state;
    private readonly ClientShopCatalog catalog;
    private readonly ClientShopPanel shop;
    private readonly ClientRequestTracker<Boundary> lifetime;
    private ClientShopPanelView? observed;
    private Dictionary<string, Row> rows = new(StringComparer.Ordinal);
    private IReadOnlyList<Row> ordered = Array.Empty<Row>();

    internal ClientAdminShopPanel(ClientSessionLifecycle session, ClientAdminAccess access, ClientShopState state,
        ClientShopCatalog catalog, ClientShopPanel shop)
    {
        this.session = session; this.access = access; this.state = state; this.catalog = catalog; this.shop = shop;
        lifetime = new(session, () => new(state.Capture(), access.CurrentPermission, session.IsUnavailable),
            boundary => ReferenceEquals(boundary.Shop, state.Capture()) && ReferenceEquals(boundary.Permission, access.CurrentPermission)
                && boundary.Unavailable == session.IsUnavailable,
            () => { observed = null; rows.Clear(); ordered = Array.Empty<Row>(); });
    }

    internal object Ownership => lifetime.Capture();
    internal string Status => shop.Status;
    internal bool CanRefresh => shop.CanRefresh;
    internal Task RefreshAsync(float now) => shop.RefreshAsync(now, manual: true);
    internal void Close() => lifetime.Reset();

    internal IReadOnlyList<Row> Read() => lifetime.Read(() =>
    {
        if (!access.IsAdministrator || session.IsUnavailable) return ordered;
        var view = shop.Read();
        if (ReferenceEquals(observed, view)) return ordered;
        var next = new Dictionary<string, Row>(view.All.Count, StringComparer.Ordinal);
        var list = new List<Row>(view.All.Count);
        foreach (var listing in view.All)
        {
            // Compare complete wire content only when the shared projection changes.
            // Preview hydration is not a product revision and must not discard typed input.
            string signature = ClientProtocolJson.Serialize(listing);
            if (!rows.TryGetValue(listing.ListingId, out var row) || row.Signature != signature)
                row = new(listing, signature);
            else row.Listing = listing;
            next[listing.ListingId] = row;
            list.Add(row);
        }
        observed = view; rows = next;
        return ordered = list.AsReadOnly();
    });

    internal bool CanChange(Row? row = null) => lifetime.Read(() =>
    {
        Read();
        return access.IsAdministrator && !session.IsUnavailable && !state.Busy
            && (row is null || (rows.TryGetValue(row.Listing.ListingId, out var current) && ReferenceEquals(current, row)));
    });
    internal bool CanSave(Row row) => CanChange(row) && row.TryBuild(out _);
    internal Task SaveAsync(Row row) => lifetime.Read(() => CanChange(row) && row.TryBuild(out var edit)
        ? catalog.UpsertAsync(edit!) : Task.CompletedTask);

    internal Func<Task> PrepareRemove(Row row)
    {
        var owner = lifetime.Capture();
        return () =>
        {
            Task work = Task.CompletedTask;
            lifetime.TryApply(owner, () => { if (CanChange(row)) work = catalog.RemoveAsync(row.Listing.ListingId); });
            return work;
        };
    }

    internal Edit PrepareEdit(Row? row = null) => lifetime.Read(() => new Edit(this, lifetime.Capture(), row));

    // Compatibility entry for standalone editors: a missing or changed product cannot become a new listing.
    internal Edit PrepareExistingEdit(ModServerShopListingDto listing) => lifetime.Read(() =>
    {
        Read();
        string signature = ClientProtocolJson.Serialize(listing);
        var row = rows.TryGetValue(listing.ListingId, out var current) && current.Signature == signature
            ? current : new Row(listing, signature);
        return PrepareEdit(row);
    });

    internal sealed record Boundary(object Shop, ClientAdminAccess.Permission Permission, bool Unavailable);

    internal sealed class Row
    {
        internal ModServerShopListingDto Listing { get; set; }
        internal string Signature { get; }
        internal string StockText { get; set; }
        internal string PriceText { get; set; }
        internal Row(ModServerShopListingDto listing, string signature)
        {
            Listing = listing; Signature = signature;
            StockText = listing.StockCount.ToString(CultureInfo.InvariantCulture);
            PriceText = ClientShopRules.BasePrice(listing).ToString(CultureInfo.InvariantCulture);
        }
        internal bool TryBuild(out ClientShopListingEdit? edit)
        {
            edit = null;
            if (Listing.Item is null || !PanelNumberInput.TryInt(StockText, out int stock) || stock < 0
                || !PanelNumberInput.TryInt(PriceText, out int price) || price < 1) return false;
            edit = new(Listing.ListingId, Listing.ListingKind, Listing.Item, price, stock,
                Listing.PriceIncreaseRatio, Listing.QualityRequirementMode, Listing.HitPointsRequirementMode);
            return true;
        }
    }

    internal sealed class Edit
    {
        private readonly ClientAdminShopPanel panel;
        private readonly ClientRequestTracker<Boundary>.Scope owner;
        private readonly Row? row;
        private readonly string? listingId;
        private bool closed;
        internal ModServerShopListingDto? Listing { get; }
        internal Edit(ClientAdminShopPanel panel, ClientRequestTracker<Boundary>.Scope owner, Row? row)
        {
            this.panel = panel; this.owner = owner; this.row = row;
            listingId = row?.Listing.ListingId;
            Listing = row is null ? null : ClientShopState.Copy(new[] { row.Listing })[0];
        }
        internal bool CanSubmit
        {
            get
            {
                bool allowed = false;
                panel.lifetime.TryApply(owner, () => allowed = !closed && panel.CanChange(row));
                return allowed;
            }
        }
        internal void Close() => closed = true;
        internal Task? Submit(ClientShopListingEdit edit)
        {
            Task? work = null;
            panel.lifetime.TryApply(owner, () =>
            {
                if (closed || !panel.CanChange(row)) return;
                closed = true;
                work = panel.catalog.UpsertAsync(edit with { ListingId = listingId });
            });
            return work;
        }
    }
}
