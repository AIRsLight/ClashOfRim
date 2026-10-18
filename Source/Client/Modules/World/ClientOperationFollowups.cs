using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Trades;

namespace AIRsLight.ClashOfRim.Modules.World;

// Completion workflows connect domains only through their public commands. The
// initiating domain supplies its original receipt; this module never captures a new login.
internal sealed class ClientOperationFollowups
{
    private readonly ClientEventRefresh events;
    private readonly ClientShopCatalog shop;
    private readonly ClientPlayerDirectory players;
    private readonly ClientWorldSynchronization world;
    private readonly IClientSessionFollowupText text;
    internal ClientOperationFollowups(ClientEventRefresh events, ClientShopCatalog shop, ClientPlayerDirectory players,
        ClientWorldSynchronization world, IClientSessionFollowupText text)
    { this.events = events; this.shop = shop; this.players = players; this.world = world; this.text = text; }

    internal Task ShopCompleted(Func<Action, bool> step)
    {
        Task pending = Task.CompletedTask, catalog = Task.CompletedTask;
        step(() => pending = events.ResumeDeferred());
        step(() => catalog = shop.RefreshAsync());
        return Task.WhenAll(pending, catalog);
    }
    internal Task TradeCompleted(ClientTradeOperation operation, Func<Action, bool> step)
    {
        if (operation != ClientTradeOperation.Create) return RefreshEvents("ClashOfRim.Trade.ReasonFulfillment", step);
        Task work = Task.CompletedTask; step(() => work = events.ResumeDeferred()); return work;
    }
    internal Task TradeAccepted(Func<Action, bool> step)
    {
        ClientEventReader.Request? read = null; Task work = Task.CompletedTask;
        if (step(() => read = events.CaptureQueueRead()))
            step(() => work = ClientSessionWork.Run(() => events.ReadQueueAsync(read!)));
        return work;
    }
    internal Task FailureReported(Func<Action, bool> step) => RefreshEvents("ClashOfRim.Events.ReasonFailureReport", step);
    private Task RefreshEvents(string key, Func<Action, bool> step)
    {
        string reason = ""; Task work = Task.CompletedTask;
        if (step(() => reason = text.Text(key))) step(() => work = events.StartAutomatic(reason));
        return work;
    }
    internal Task BaselineUpdated(Func<Action, bool> step) => RefreshWorld("ClashOfRim.Admin.WorldBaselineRefreshReason", false, step);
    internal Task DiplomacyFinished(Func<Action, bool> step) => RefreshWorld("ClashOfRim.Diplomacy.RefreshReasonRelationChanged", true, step);
    private Task RefreshWorld(string key, bool refreshPlayers, Func<Action, bool> step)
    {
        string reason = ""; Task directory = Task.CompletedTask, markers = Task.CompletedTask;
        if (step(() => reason = text.Text(key)))
        {
            if (refreshPlayers) step(() => directory = players.RefreshAsync(manual: false));
            step(() => markers = world.RequestAsync(ClientWorldSynchronizationKind.Markers, reason));
        }
        return Task.WhenAll(directory, markers);
    }
}
