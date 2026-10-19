using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld.Planet;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartRefreshEventsForTest() => _ = eventRefresh.StartManual(ClientEventReadKind.Refresh);

    internal void StartManualLogin() => _ = sessionStartup.StartAsync(ClientSessionStartKind.Manual);

    internal void StartManualPresence() => _ = sessionNotifications.Start();

    internal void StopManualPresence() => sessionConnection.Stop();

    internal void DisconnectMultiplayerSessionForExit() => _ = sessionLifecycle.End(sendLogout: true);

    internal void StartRefreshChatMessages(bool initialLoad = false) => _ = chat.RefreshAsync(initialLoad);
    internal void StartSendChatMessage(string rawText) => _ = chat.SendAsync(rawText);
    internal void MarkPrivateChatRead() => chat.MarkPrivateRead();

    internal void StartSyncWorldMapMarkers() => RequestWorldMapMarkerRefresh("manual-refresh-world-map");
    internal void ApplyCachedWorldMapMarkersToWorldObjects() => worldMarkers.ApplyCached();
    internal void RequestWorldMapMarkerRefresh(string reason)
        => _ = worldSynchronization.RequestAsync(ClientWorldSynchronizationKind.Markers, reason);
    internal void StartRefreshCaravanArrivalTargets(string caravanTileSignature)
        => _ = worldSynchronization.RequestAsync(ClientWorldSynchronizationKind.CaravanTargets, "caravan-target-refresh", caravanTileSignature);
    internal void StartSyncRuntimeWorldObjects()
        => _ = worldSynchronization.RequestAsync(ClientWorldSynchronizationKind.RuntimeObjects, "runtime-world-object-sync");

    internal void StartSyncWorldConfigurationExtensions()
        => _ = colonyRegistration.StartAsync(ClientColonyRegistrationKind.Extensions);

    internal void RequestWorldConfigurationExtensionSync(string reason)
        => _ = colonyRegistration.RequestAsync(ClientColonyRegistrationKind.Extensions, reason);

    internal void OpenAbandonPlayerColonyConfirmation(MapParent colony)
        => colonyAbandonment.Open(new ClientColonyAbandonTarget(colony));
}
