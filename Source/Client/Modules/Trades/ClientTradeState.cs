using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Trades;

// Owns market state and page identity. No separate session generation or cache lock:
// publication and identity invalidation use the existing session boundary.
internal sealed class ClientTradeState
{
    private readonly ClientSessionLifecycle session;
    private readonly IClientSnapshotGame game;
    private readonly IClientTradeMarketPresentation presentation;
    private Scope? owner;
    private Page? page;
    private ClientSynchronizationCoordinator.Lease? work;
    private readonly List<ModTradeOrderSummaryDto> orders = new();
    private string? message;
    private string scope = "Open";
    private bool enabled = true, hasMore;
    private int version, total, nextOffset;

    internal ClientTradeState(ClientSessionLifecycle session, IClientSnapshotGame game, IClientTradeMarketPresentation presentation)
    { this.session = session; this.game = game; this.presentation = presentation; }

    internal string Message { get => Read(() => message ?? presentation.InitialStatus); set => Read(() => message = value); }
    internal string SelectedScope => Read(() => scope);
    internal bool Enabled => Read(() => enabled);
    internal bool HasMore => Read(() => hasMore);
    internal bool Loading => Read(() => page is not null);
    internal int Version => Read(() => version);
    internal int Total => Read(() => total);
    internal int Count => Read(() => orders.Count);
    internal List<ModTradeOrderSummaryDto> CopyOrders() => Read(() => Copy(orders));
    internal Scope Capture() => Read(() => owner!);
    internal void Track(ClientSynchronizationCoordinator.Lease lease) => Read(() => work = lease);
    internal void Reset() { using var p = ClientSessionWork.EnterPublication(); owner = null; Normalize(); }

    private T Read<T>(Func<T> read)
    { using var p = ClientSessionWork.EnterPublication(); Normalize(); return read(); }

    private void Normalize()
    {
        if (owner is not null && Owns(owner)) return;
        work?.Dispose(); work = null;
        owner = new(session.CaptureOwnership(), session.ReadContext(), game.BindCurrentGame());
        page = null; orders.Clear(); scope = "Open"; enabled = true; hasMore = false; total = nextOffset = 0;
        message = null; version++;
    }

    private bool Owns(Scope candidate) => candidate.Game.IsCurrent && session.Owns(candidate.Owner, candidate.Context, false);
    internal bool TryApply(Scope candidate, Action action, ClashOfRimClientNetworkContext? basis = null)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() =>
        {
            Normalize();
            if (!ReferenceEquals(owner, candidate) || !Owns(candidate)
                || (basis is not null && !session.Owns(candidate.Owner, basis, true))) return;
            action(); applied = true;
        });
        return applied;
    }

    internal Page? BeginPage(Scope candidate, string requestedScope, bool reset)
    {
        Page? request = null;
        TryApply(candidate, () =>
        {
            if (!reset && (page is not null || !hasMore || scope != requestedScope)) return;
            if (reset)
            {
                orders.Clear(); scope = requestedScope; hasMore = false; total = nextOffset = 0; version++;
            }
            request = page = new(candidate, session.ReadContext(), requestedScope, nextOffset, requestedScope == "History" ? 5 : 10);
        });
        return request;
    }

    internal bool TryApply(Page request, Action action)
    {
        bool applied = false;
        TryApply(request.Scope, () => { if (!ReferenceEquals(page, request)) return; action(); applied = true; }, request.Context);
        return applied;
    }

    internal void EndPage(Page request) => TryApply(request.Scope, () => { if (ReferenceEquals(page, request)) page = null; });

    // Called within the owning page publication. Copy inbound DTOs before storing;
    // callers and preview adapters cannot mutate the market through shared objects.
    internal void PublishPage(Page request, ModListTradeOrdersResponseDto response)
    {
        enabled = response.TradeMarketplaceEnabled;
        bool showOrders = enabled || request.Filter == "Mine";
        var incoming = showOrders ? Copy(response.Orders ?? new()) : new List<ModTradeOrderSummaryDto>();
        if (!showOrders) orders.Clear();
        foreach (var order in incoming)
        {
            int index = orders.FindIndex(old => old.EventId == order.EventId);
            if (index < 0) orders.Add(order); else orders[index] = order;
        }
        // Advance by rows consumed, not distinct IDs displayed: adjacent pages can overlap.
        nextOffset = request.Offset + incoming.Count;
        hasMore = showOrders && response.HasMore && incoming.Count > 0;
        total = Math.Max(0, response.TotalCount);
        version++;
        message = !enabled ? presentation.Text("Disabled") : orders.Count == 0 ? presentation.Text("NoAcceptableOrders")
            : presentation.Text("PageStatus", ("LOADED", orders.Count), ("TOTAL", Math.Max(total, orders.Count)));
    }

    internal void Accepted(string eventId, string? memoId) => ChangeOrder(eventId, order =>
    {
        if (!order.ViewerHasAccepted) order.AcceptedMemoCount++;
        order.ViewerHasAccepted = true;
        if (!string.IsNullOrWhiteSpace(memoId)) order.ViewerAcceptedMemoEventId = memoId;
    });
    internal void Fulfilled(string eventId) => ChangeOrder(eventId, order => order.Status = "AppliedToSnapshot");
    internal void Claimed(string eventId, string status) => ChangeOrder(eventId, order => order.Status = status);
    internal void Cancelled(string eventId, bool acceptedMemo) => Read(() =>
    {
        page = null;
        var order = orders.FirstOrDefault(o => o.EventId == eventId);
        if (order is null) return false;
        if (acceptedMemo)
        {
            order.ViewerHasAccepted = false; order.ViewerAcceptedMemoEventId = null;
            order.AcceptedMemoCount = Math.Max(0, order.AcceptedMemoCount - 1);
        }
        if (!acceptedMemo || scope == "AcceptedByMe") { orders.Remove(order); total = Math.Max(0, total - 1); }
        version++; return true;
    });
    private void ChangeOrder(string eventId, Action<ModTradeOrderSummaryDto> change) => Read(() =>
    {
        page = null;
        var order = orders.FirstOrDefault(o => o.EventId == eventId);
        if (order is null) return false;
        change(order); version++; return true;
    });

    private static List<ModTradeOrderSummaryDto> Copy(List<ModTradeOrderSummaryDto> source) =>
        ClientProtocolJson.Deserialize<List<ModTradeOrderSummaryDto>>(ClientProtocolJson.Serialize(source), static type => type);

    internal sealed record Scope(ClientSessionLifecycle.Ownership Owner, ClashOfRimClientNetworkContext Context, IClientSnapshotGameSession Game);
    internal sealed record Page(Scope Scope, ClashOfRimClientNetworkContext Context, string Filter, int Offset, int Limit);
}
