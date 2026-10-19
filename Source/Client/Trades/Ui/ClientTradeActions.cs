using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal sealed class ClientTradeActions : IClientTradeInteraction
{
    private readonly ClientTradeOperations operations;
    private readonly ClientTradeState state;
    private readonly ClientSessionLifecycle session;
    private readonly IClientTradeOperationsTransport transport;
    private readonly Func<IReadOnlyList<ModWorldMapMarkerDto>> markers;
    private readonly Func<float?> feeRate;
    private readonly Func<IReadOnlyList<ModAdminFixedTradeFeeDto>?> fixedFees;
    internal ClientTradeActions(ClientTradeOperations operations, ClientTradeState state, ClientSessionLifecycle session,
        IClientTradeOperationsTransport transport, Func<IReadOnlyList<ModWorldMapMarkerDto>> markers,
        Func<float?> feeRate, Func<IReadOnlyList<ModAdminFixedTradeFeeDto>?> fixedFees)
    { this.operations = operations; this.state = state; this.session = session; this.transport = transport;
        this.markers = markers; this.feeRate = feeRate; this.fixedFees = fixedFees; }
    internal ClashOfRimClientNetworkContext Context => session.ReadContext();
    internal float? FeeRate => feeRate();
    internal IReadOnlyList<ModAdminFixedTradeFeeDto>? FixedFees => fixedFees();
    internal ClientTradeState.Scope Capture() => state.Capture();
    internal bool Owns(ClientTradeState.Scope owner) => state.TryApply(owner, static () => { });
    internal Action Bind(ClientTradeState.Scope owner, Action action) => () => state.TryApply(owner, action);
    internal void Enqueue(ClientTradeState.Scope owner, Action action) => owner.Game.Enqueue(Bind(owner, action), static () => { });
    internal Task<ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto>> QuoteAsync(ClientTradeState.Scope owner,
        IReadOnlyList<ModThingReferenceDto> offered, IReadOnlyList<ModThingReferenceDto> requested)
    {
        var task = Task.FromResult(ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto>.Failed("Obsolete", ""));
        state.TryApply(owner, () => task = transport.QuoteAsync(Context, offered, requested, owner.Owner.Token));
        return task;
    }
    internal void Create(ClientTradeState.Scope owner, Map map, TradeOrderDraft draft) => state.TryApply(owner, () =>
        _ = operations.StartAsync(new ClientTradeDraftSource(map, draft), new()
        {
            OfferedThings = draft.OfferedThings.ToList(), RequestedThings = draft.RequestedThings.ToList(), FeeSilver = draft.FeeSilver,
            AllowSelfPickup = draft.AllowSelfPickup, AllowServerDropPod = draft.AllowServerDropPod
        }));
    public void FulfillByDropPod(ModTradeOrderSummaryDto order) => _ = operations.StartAsync(new ClientTradeDropPodSource(Find.CurrentMap), order);
    internal void OpenMarket(Func<MultiplayerTradePage> createPage)
    {
        var context = Context;
        if (!context.IsConfigured || string.IsNullOrWhiteSpace(context.CurrentSnapshotId))
        { state.Message = ClashOfRimText.Key("ClashOfRim.Trade.StatusConfigMissingMarket"); return; }
        Find.WindowStack.Add(new TradeMarketWindow(createPage()));
    }
    public void OpenCreate()
    {
        if (!state.Enabled) { Messages.Message(ClashOfRimText.Key("ClashOfRim.Trade.Disabled"), MessageTypeDefOf.RejectInput, false); return; }
        if (Find.CurrentMap is not { } map) { state.Message = ClashOfRimText.Key("ClashOfRim.Trade.StatusNoMapCreate"); return; }
        if (!Context.IsConfigured || string.IsNullOrWhiteSpace(Context.CurrentSnapshotId))
        { state.Message = ClashOfRimText.Key("ClashOfRim.Trade.StatusConfigMissingCreate"); return; }
        Find.WindowStack.Add(new TradeOrderDialogWindow(this, map));
    }
    public void ConfirmGoods(ModTradeOrderSummaryDto order, Action confirmed)
    {
        var action = Bind(Capture(), confirmed);
        if (order.OfferedThings is null || order.OfferedThings.All(t => t.StackCount <= 0))
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(ClashOfRimText.Key("ClashOfRim.Trade.ConfirmNoOfferedThings"), action));
        else action();
    }
    internal IReadOnlyList<ModTradeOrderSummaryDto> FindFulfillable(Caravan caravan)
    {
        if (!Context.IsConfigured || string.IsNullOrWhiteSpace(Context.CurrentSnapshotId)) return Array.Empty<ModTradeOrderSummaryDto>();
        var world = markers();
        return state.CopyOrders().Where(o => ClientTradeTargets.CanSelfDeliver(o, ClientTradeTargets.Resolve(o, world), caravan.Tile,
            RemoteWorldObjectUiUtility.ReadTileLayerId(caravan.Tile))).ToArray();
    }
    internal void OpenCaravanMenu(Caravan caravan, IReadOnlyList<ModTradeOrderSummaryDto> orders)
    {
        if (orders.Count == 0) { state.Message = ClashOfRimText.Key("ClashOfRim.Trade.StatusNoFulfillableCaravanOrders"); return; }
        var owner = Capture();
        var context = Context;
        var inventory = TradeCaravanFulfillmentUtility.BuildDeliveredThingReferences(caravan, context.UserId, context.ColonyId, context.CurrentSnapshotId ?? "");
        var world = markers();
        var options = new List<FloatMenuOption>();
        foreach (var order in orders)
        {
            string label = ClashOfRimText.Key("ClashOfRim.Trade.CaravanFulfillOption",
                (order.Owner is null ? ClashOfRimText.Key("ClashOfRim.Trade.UnknownOwner") : $"{order.Owner.UserId}/{order.Owner.ColonyId}").Named("OWNER"),
                TradeUiUtility.FormatThingList(order.RequestedThings, true).Named("DELIVER"),
                TradeUiUtility.FormatThingList(order.OfferedThings, false).Named("RECEIVE"));
            if (!TradeCaravanFulfillmentUtility.Satisfies(order.RequestedThings, inventory, out var missing))
            {
                options.Add(new FloatMenuOption(label + ClashOfRimText.Key("ClashOfRim.Trade.CaravanMissingSuffix"), null)
                    { Disabled = true, tooltip = string.Join("\n", missing.Take(5)) }); continue;
            }
            var target = ClientTradeTargets.Resolve(order, world);
            options.Add(new FloatMenuOption(label, Bind(owner, () =>
            {
                string summary = ClashOfRimText.Key("ClashOfRim.Trade.ConfirmCaravanFulfillTitle") + "\n\n"
                    + ClashOfRimText.Key("ClashOfRim.Trade.DeliverLine", TradeUiUtility.FormatThingList(order.RequestedThings, true).Named("THINGS")) + "\n"
                    + ClashOfRimText.Key("ClashOfRim.Trade.ReceiveLine", TradeUiUtility.FormatThingList(order.OfferedThings, false).Named("THINGS"));
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(summary, Bind(owner, () => ConfirmGoods(order,
                    () => _ = operations.StartAsync(new ClientTradeCaravanSource(caravan, target), order)))));
            })));
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }
}
