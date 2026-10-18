using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Presence;

internal interface IClientSessionNotificationEffects
{
    void RefreshChat(bool initial);
    void RefreshLedger(string reason);
    void RefreshWorld(string reason);
    void Expired(ClashOfRimClientNetworkContext context, string sessionId);
}
internal interface IClientSessionNotificationPresentation
{
    string Text(string key, params (string Name, object? Value)[] args);
    void Status(string text);
    void Enqueue(Action action, Action discard);
    void Message(string text);
    void Diagnose(string eventName, Exception error);
}

// Decodes and dispatches stream notifications under both the original connection
// revision and authenticated identity. Queued effects retain those same proofs.
internal sealed class ClientSessionNotifications
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSessionConnection connection;
    private readonly ClientEventState events;
    private readonly ClientRequestTracker state;
    private readonly IClientSessionNotificationEffects effects;
    private readonly IClientSessionNotificationPresentation view;
    private long worldVersion;
    internal ClientSessionNotifications(ClientSessionLifecycle session, ClientSessionConnection connection, ClientEventState events,
        IClientSessionNotificationEffects effects, IClientSessionNotificationPresentation view)
    {
        this.session = session; this.connection = connection; this.events = events; this.effects = effects; this.view = view;
        state = new(session, () => worldVersion = 0);
        connection.EventReceived += Receive;
    }
    internal long WorldVersion => state.Read(() => worldVersion);
    internal void Reset() => state.Reset();
    internal Task Start()
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = state.Capture(); var context = session.ReadContext(); string? id = session.SessionId;
        long ledger = events.NotificationVersion;
        return state.TryApply(scope, static () => { }) && !session.IsUnavailable
            ? connection.Start(context, id, ledger, worldVersion) : Task.CompletedTask;
    }
    private void Receive(ModSessionStreamEvent item, ClientSessionConnection.Observation source)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = state.Capture();
        bool Apply(Action action)
        {
            bool applied = false;
            source.TryApply(() => state.TryApply(scope, () =>
            {
                if (session.IsUnavailable || session.SessionId != source.SessionId || !session.Owns(scope.Owner, source.Context, false)) return;
                action(); applied = true;
            }));
            return applied;
        }
        bool Current() => Apply(static () => { });
        bool Step(Action action) => Apply(action) && Current();
        string? Text(string key, params (string Name, object? Value)[] args)
        { string? value = null; return Step(() => value = view.Text(key, args)) ? value : null; }
        bool Status(string key, params (string Name, object? Value)[] args)
        { string? text = Text(key, args); return text is not null && Step(() => view.Status(text)); }
        void Failure(Exception error)
        {
            if (Status("ClashOfRim.Presence.StatusException", ("TYPE", error.GetType().Name), ("MESSAGE", error.Message)))
                Step(() => view.Diagnose(item.EventName, error));
        }
        try
        {
            if (!Current()) return;
            var notice = ClientSessionNotification.Decode(item);
            if (notice is null || notice.User != source.Context.UserId) return;
            long version = 0;
            if (notice.Kind is ClientSessionNotificationKind.Presence or ClientSessionNotificationKind.Ledger or ClientSessionNotificationKind.Error)
            {
                if (!Step(() => events.ObserveNotificationVersion(notice.Version)) || !Step(() => version = events.NotificationVersion)) return;
            }
            switch (notice.Kind)
            {
                case ClientSessionNotificationKind.Presence:
                    if (notice.Online == true && Status("ClashOfRim.Presence.StatusConnected", ("VERSION", version))) Step(() => effects.RefreshChat(true));
                    break;
                case ClientSessionNotificationKind.Ledger:
                    if (!Status("ClashOfRim.Presence.StatusLedgerChanged", ("VERSION", version))) break;
                    string? queue = Text("ClashOfRim.Presence.StatusLedgerChangedSyncing");
                    if (queue is null || !Step(() => events.SetQueueStatus(queue))) break;
                    string? ledgerReason = Text("ClashOfRim.Presence.ReasonLedgerChanged");
                    if (ledgerReason is not null) Step(() => effects.RefreshLedger(ledgerReason));
                    break;
                case ClientSessionNotificationKind.Chat:
                    if (Status("ClashOfRim.Presence.StatusChatChanged")) Step(() => effects.RefreshChat(false));
                    break;
                case ClientSessionNotificationKind.World:
                    worldVersion = Math.Max(worldVersion, notice.Version);
                    if (!Status("ClashOfRim.Presence.StatusWorldConfigurationChanged", ("VERSION", worldVersion))) break;
                    string? worldReason = Text("ClashOfRim.Presence.ReasonWorldConfigurationChanged");
                    if (worldReason is not null) Step(() => effects.RefreshWorld(worldReason));
                    break;
                case ClientSessionNotificationKind.Player:
                    if (notice.Player == source.Context.UserId) break;
                    string? player = Text("ClashOfRim.Presence.PlayerOnline", ("PLAYER", notice.Player));
                    if (player is null) break;
                    bool consumed = false;
                    Step(() => view.Enqueue(() =>
                    {
                        try { Step(() => { if (consumed) return; consumed = true; view.Message(player); }); }
                        catch (Exception ex) { Failure(ex); }
                    }, () => consumed = true));
                    break;
                case ClientSessionNotificationKind.Error:
                    if (Status("ClashOfRim.Presence.StatusStreamError", ("MESSAGE", notice.Message)) && notice.Online == false)
                        Step(() => effects.Expired(source.Context, source.SessionId));
                    break;
            }
        }
        catch (Exception ex) { Failure(ex); }
    }
}
