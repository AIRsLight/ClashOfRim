using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld.Planet;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartRefreshEventsForTest() => _ = eventRefresh.StartManual(ClientEventReadKind.Refresh);

    internal void StartManualLogin() => _ = sessionStartup.StartAsync(ClientSessionStartKind.Manual);

    internal void StartManualPresence() => _ = sessionNotifications.Start();

    internal void StopManualPresence() => sessionConnection.Stop();

    internal void DisconnectMultiplayerSessionForExit()
    {
        _ = sessionLifecycle.End(sendLogout: true);
        presenceStatus = ClashOfRimText.Key("ClashOfRim.Presence.StatusSessionDisconnected");
    }

    private void ClearTransientMultiplayerSessionStateForExit()
    {
        TradePawnPreviewService.Reset();
        colonyRegistration.Reset();
        bankState.Reset();
        mercenaryState.Reset();
        mercenaryIncidents.Reset();
        shopState.Reset();
        tradeState.Reset();
        chat.Reset();
        RemoteColonyWorldIconCache.Clear();
        worldMarkers.Reset();
        worldSynchronization.Reset();
        settings.CurrentWorldConfigurationId = string.Empty;
        administration.Reset();
        sessionNotifications.Reset();
        worldSetup.Reset();
        settings.CurrentSnapshotId = string.Empty;
        settings.CurrentLineageToken = string.Empty;
        playerDirectory.ClearSelection();
        playerDirectory.Reset();

        occupiedColonySites.Reset();
    }

    internal void StartRefreshChatMessages(bool initialLoad = false) => _ = chat.RefreshAsync(initialLoad);
    internal void StartSendChatMessage(string rawText) => _ = chat.SendAsync(rawText);
    internal void MarkPrivateChatRead() => chat.MarkPrivateRead();

    internal void StartSyncWorldMapMarkers() => RequestWorldMapMarkerRefresh("manual-refresh-world-map");
    internal void RequestWorldMapMarkerRefresh(string reason)
        => _ = worldSynchronization.RequestAsync(ClientWorldSynchronizationKind.Markers, reason);
    internal void StartRefreshCaravanArrivalTargets(string caravanTileSignature)
        => _ = worldSynchronization.RequestAsync(ClientWorldSynchronizationKind.CaravanTargets, "caravan-target-refresh", caravanTileSignature);
    internal void StartSyncRuntimeWorldObjects()
        => _ = worldSynchronization.RequestAsync(ClientWorldSynchronizationKind.RuntimeObjects, "runtime-world-object-sync");
    internal void PollWorldSynchronization(int ticks) => _ = worldSynchronization.Poll(ticks);

    internal void StartRegisterPlayerColonySites()
        => _ = colonyRegistration.StartAsync(ClientColonyRegistrationKind.Sites, colonyAppearance.Capture());

    internal void RequestPlayerColonySiteRegistration(string reason, bool suppressWorldConfigurationNotification = false)
        => _ = colonyRegistration.RequestAsync(ClientColonyRegistrationKind.Sites, reason, colonyAppearance.Capture(), suppressWorldConfigurationNotification);

    internal void StartSyncWorldConfigurationExtensions()
        => _ = colonyRegistration.StartAsync(ClientColonyRegistrationKind.Extensions);

    internal void RequestWorldConfigurationExtensionSync(string reason)
        => _ = colonyRegistration.RequestAsync(ClientColonyRegistrationKind.Extensions, reason);

    internal void OpenAbandonPlayerColonyConfirmation(MapParent colony)
        => colonyAbandonment.Open(new ClientColonyAbandonTarget(colony));

    internal void RequestServerWorldBaselineRefresh(string reason)
    {
        StartRefreshWorldConfigurationCatalog(reason, includeGenerationBaseline: true);
    }

    private void StartRefreshWorldConfigurationCatalog(string reason, bool includeGenerationBaseline = false)
        => _ = worldCatalog.RefreshAsync(reason, includeGenerationBaseline);

    internal void StartMapServerSession() => _ = sessionStartup.StartAsync(ClientSessionStartKind.Map);
}
