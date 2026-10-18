using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Presence;

namespace AIRsLight.ClashOfRim.Modules.World;

internal enum ClientSessionFollowup { Markers, Events, Players, Chat, ColonySites, Extensions, Presence }
internal enum ClientSessionFollowupSource { ManualLogin, MapLogin, InitialWorld, SnapshotReload }
internal interface IClientSessionFollowups
{
    void AdoptQueue(ModEventQueueSummaryDto? queue);
    Task Dispatch(ClientSessionFollowup effect, ClientSessionFollowupSource source, Func<Action, bool> step);
}
internal interface IClientSessionFollowupText { string Text(string key); }

// Workflows own ordering and admission release. Dispatch keeps their exact source
// check across translation/capture, before the destination domain starts its own work.
internal sealed class ClientSessionFollowups : IClientSessionFollowups
{
    private readonly ClientEventRefresh events;
    private readonly ClientPlayerDirectory players;
    private readonly ClientChat chat;
    private readonly ClientColonyRegistration registration;
    private readonly ClientColonyAppearance appearance;
    private readonly ClientWorldSynchronization world;
    private readonly ClientSessionNotifications notifications;
    private readonly IClientSessionFollowupText text;
    internal ClientSessionFollowups(ClientEventRefresh events, ClientPlayerDirectory players, ClientChat chat,
        ClientColonyRegistration registration, ClientColonyAppearance appearance, ClientWorldSynchronization world,
        ClientSessionNotifications notifications, IClientSessionFollowupText text)
    {
        this.events = events; this.players = players; this.chat = chat; this.registration = registration;
        this.appearance = appearance; this.world = world; this.notifications = notifications; this.text = text;
    }
    public void AdoptQueue(ModEventQueueSummaryDto? queue) => events.AdoptQueue(queue);
    public Task Dispatch(ClientSessionFollowup effect, ClientSessionFollowupSource source, Func<Action, bool> step)
    {
        Task work = Task.CompletedTask;
        string reason = "";
        bool Reason(string key) => step(() => reason = text.Text(key));
        switch (effect)
        {
            case ClientSessionFollowup.Markers:
                if (Reason("ClashOfRim.WorldBaseline.ReasonSubmitted"))
                    step(() => work = world.RequestAsync(ClientWorldSynchronizationKind.Markers, reason));
                break;
            case ClientSessionFollowup.Events:
                string key = source switch
                {
                    ClientSessionFollowupSource.ManualLogin => "ClashOfRim.Login.ReasonManual",
                    ClientSessionFollowupSource.MapLogin => "ClashOfRim.MapSession.ReasonLogin",
                    ClientSessionFollowupSource.InitialWorld => "ClashOfRim.WorldBaseline.ReasonInitialSnapshotUploaded",
                    ClientSessionFollowupSource.SnapshotReload => "ClashOfRim.ServerEntry.ReasonServerSnapshotLoaded",
                    _ => throw new ArgumentOutOfRangeException(nameof(source))
                };
                if (Reason(key)) step(() => work = events.StartAutomatic(reason));
                break;
            case ClientSessionFollowup.Players:
                step(() => work = players.RefreshAsync(manual: false)); break;
            case ClientSessionFollowup.Chat:
                step(() => work = chat.RefreshAsync(initial: true)); break;
            case ClientSessionFollowup.ColonySites:
                if (source != ClientSessionFollowupSource.InitialWorld && !Reason(source == ClientSessionFollowupSource.SnapshotReload
                    ? "ClashOfRim.ServerEntry.ReasonServerSnapshotLoaded" : "ClashOfRim.MapSession.ReasonLoginSucceeded")) break;
                ModColonyAppearanceDto? captured = null;
                if (step(() => captured = appearance.Capture()))
                    step(() => work = source == ClientSessionFollowupSource.InitialWorld
                        ? registration.StartAsync(ClientColonyRegistrationKind.Sites, captured)
                        : registration.RequestAsync(ClientColonyRegistrationKind.Sites, reason, captured));
                break;
            case ClientSessionFollowup.Extensions:
                step(() => work = registration.StartAsync(ClientColonyRegistrationKind.Extensions)); break;
            case ClientSessionFollowup.Presence:
                step(() => work = notifications.Start()); break;
            default: throw new ArgumentOutOfRangeException(nameof(effect));
        }
        return work;
    }
}
