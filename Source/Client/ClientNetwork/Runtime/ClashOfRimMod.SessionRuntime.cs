using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartRefreshEventsForTest() => _ = eventRefresh.StartManual(ClientEventReadKind.Refresh);

    internal void StartManualLogin() => _ = sessionStartup.StartAsync(ClientSessionStartKind.Manual);

    internal void StartManualPresence() => _ = sessionNotifications.Start();

    internal void StopManualPresence() => sessionConnection.Stop();

    private void PresentConnectionStatus(SessionConnectionStatus status)
    {
        presenceStatus = status.Phase switch
        {
            SessionConnectionPhase.NotConfigured => ClashOfRimText.Key("ClashOfRim.Presence.StatusNotConfigured"),
            SessionConnectionPhase.SessionMissing => ClashOfRimText.Key("ClashOfRim.Presence.StatusSessionMissing"),
            SessionConnectionPhase.Connecting => ClashOfRimText.Key("ClashOfRim.Presence.StatusConnecting"),
            SessionConnectionPhase.Disconnecting => ClashOfRimText.Key("ClashOfRim.Presence.StatusDisconnecting"),
            SessionConnectionPhase.Cancelled => ClashOfRimText.Key("ClashOfRim.Presence.StatusDisconnectedManual"),
            SessionConnectionPhase.Closed => ClashOfRimText.Key("ClashOfRim.Presence.StatusClosed"),
            SessionConnectionPhase.Failed => ClashOfRimText.Key("ClashOfRim.Presence.StatusFailed",
                (status.ErrorCode ?? string.Empty).Named("CODE"), (status.Message ?? string.Empty).Named("MESSAGE")),
            SessionConnectionPhase.Exception => ClashOfRimText.Key("ClashOfRim.Presence.StatusException",
                status.Error!.GetType().Name.Named("TYPE"), status.Error.Message.Named("MESSAGE")),
            _ => presenceStatus
        };
        if (status.Phase == SessionConnectionPhase.SessionMissing)
            Log.Warning("[ClashOfRim] Cannot start session stream without session id.");
        else if (status.Phase == SessionConnectionPhase.Connecting)
            ClashLog.Message("[ClashOfRim] Starting WebSocket session stream: user=" + settings.UserId
                + ", colony=" + settings.ColonyId + ", session=" + status.SessionId);
        else if (status.Phase == SessionConnectionPhase.Exception)
            Log.Warning("[ClashOfRim] Session stream failed: " + status.Error);
    }

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

    private static bool IsNetworkCancellationFailure<T>(ClashOfRimClientNetworkResult<T> result)
    {
        return string.Equals(result.ErrorCode, nameof(TaskCanceledException), StringComparison.Ordinal);
    }

    private void HandleSessionExpired(string? message, string? observedAuthToken = null, string? observedSessionId = null)
    {
        HandleSessionUnavailable(
            ClashOfRimText.Key("ClashOfRim.SessionExpired.Title"),
            ClashOfRimText.Key("ClashOfRim.SessionExpired.Message"),
            ClashOfRimText.Key("ClashOfRim.SessionExpired.Status"),
            observedAuthToken,
            observedSessionId);
    }

    private void HandleSessionDisconnected(string observedSessionId)
    {
        if (Current.ProgramState != ProgramState.Playing
            || Current.Game is null
            || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            return;
        }

        HandleSessionUnavailable(
            ClashOfRimText.Key("ClashOfRim.SessionExpired.ConnectionLostTitle"),
            ClashOfRimText.Key("ClashOfRim.SessionExpired.ConnectionLostMessage"),
            ClashOfRimText.Key("ClashOfRim.SessionExpired.ConnectionLostStatus"),
            observedAuthToken: null,
            observedSessionId);
    }

    private void HandleSessionUnavailable(
        string dialogTitle,
        string dialogMessage,
        string status,
        string? observedAuthToken,
        string? observedSessionId)
    {
        var invalidation = sessionLifecycle.BeginUnavailable(observedAuthToken, observedSessionId);
        if (invalidation is null) return;
        sessionLifecycle.WhenUnavailableReady(invalidation, () => ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
        {
            using var publication = ClientSessionWork.EnterPublication();
            if (!sessionLifecycle.CompleteUnavailable(invalidation)) return;
            loginStatus = status;
            presenceStatus = loginStatus;
            Find.MainTabsRoot?.SetCurrentTab(null);
            if (Find.WindowStack.WindowOfType<SessionExpiredWindow>() is null)
            {
                Find.WindowStack.Add(new SessionExpiredWindow(
                    dialogTitle,
                    dialogMessage,
                    StartSessionReconnect,
                    GenScene.GoToMainMenu));
            }
        }));
    }

    private void StartSessionReconnect(SessionExpiredWindow window)
    {
        using var publication = ClientSessionWork.EnterPublication();
        SessionReconnectBlockReason blocked = sessionReconnect.TryBegin(new SessionReconnectLocalState(
            Current.ProgramState == ProgramState.Playing, Current.Game is not null, localMutations.IsPending), out var attempt);
        if (attempt is null)
        {
            window.CompleteReconnect(false, FormatSessionReconnectBlockReason(blocked));
            return;
        }
        var observation = worldLoginPublication.Capture();
        ClashLog.Message("[ClashOfRim][SessionReconnect] Starting in-place reconnect: user=" + attempt.Expected.UserId
            + ", colony=" + attempt.Expected.ColonyId + ", snapshot=" + attempt.Expected.CurrentSnapshotId
            + ", activeRaid=" + ClashOfRimGameComponent.HasActiveRaidBattleSession
            + ", observation=" + ClashOfRimGameComponent.HasActiveObservationSession + ".");
        _ = RunSessionReconnect(attempt, window, Current.Game, observation);
    }

    private async Task RunSessionReconnect(ClientSessionReconnect.Attempt attempt, SessionExpiredWindow window, Game? originalGame, ClientWorldMarkers.Observation observation)
    {
        try
        {
            await ClientSessionWork.Run(async () =>
            {
                await attempt.PrepareAsync();
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cancellation = attempt.Owner.Token.Register(() => completion.TrySetCanceled(attempt.Owner.Token));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    try
                    {
                        using var publication = ClientSessionWork.EnterPublication();
                        var result = attempt.Complete(Current.ProgramState == ProgramState.Playing,
                            originalGame is not null && ReferenceEquals(Current.Game, originalGame), localMutations.IsPending);
                        PresentSessionReconnect(window, attempt.Expected, result, observation);
                        completion.TrySetResult(true);
                    }
                    catch (Exception ex) { completion.TrySetException(ex); }
                }, () => completion.TrySetResult(false));
                await completion.Task;
            });
        }
        catch (Exception ex) when (attempt.Owner.Token == ClientSessionWork.Token && ClientSessionWork.IsCurrent)
        {
            Log.Warning("[ClashOfRim] Session reconnect completion failed: " + ex);
            ClashOfRimGameComponent.EnqueueMainThreadAction(() => window.CompleteReconnect(false,
                ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectNetworkFailed", ex.Message.Named("MESSAGE"))));
        }
        finally
        {
            await attempt.DisposeAsync();
        }
    }

    private void PresentSessionReconnect(SessionExpiredWindow window, ClashOfRimClientNetworkContext expected, SessionReconnectResult result, ClientWorldMarkers.Observation observation)
    {
        if (result.Outcome == SessionReconnectOutcome.Obsolete) return;
        if (result.Outcome != SessionReconnectOutcome.Succeeded)
        {
            string status = result.Outcome switch
            {
                SessionReconnectOutcome.LocalStateBlocked => FormatSessionReconnectBlockReason(result.BlockReason),
                SessionReconnectOutcome.IdentityChanged => ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectIdentityChanged"),
                SessionReconnectOutcome.SnapshotChanged => ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectSnapshotChanged"),
                SessionReconnectOutcome.Rejected => ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectRejected", (result.Message ?? string.Empty).Named("MESSAGE")),
                _ => ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectNetworkFailed", (result.Message ?? string.Empty).Named("MESSAGE"))
            };
            Log.Warning("[ClashOfRim][SessionReconnect] Reconnect refused: " + result.Outcome + ": " + result.Message);
            window.CompleteReconnect(false, status);
            return;
        }

        if (result.Publication is not { } publication || worldLoginPublication.Adopt(observation, publication, result.Login!) is not { } acceptedLogin) return;
        ModLoginResponseDto login = result.Login!;
        ClashLog.Message("[ClashOfRim][SessionReconnect] In-place reconnect accepted: user=" + expected.UserId
            + ", colony=" + expected.ColonyId + ", snapshot=" + expected.CurrentSnapshotId + ", session=" + login.SessionId
            + ", activeRaidRecovery=" + (result.Snapshot!.ActiveRaidRecovery?.EventId ?? "<none>") + ".");
        loginStatus = ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectSucceeded");
        presenceStatus = loginStatus;
        eventRefresh.AdoptQueue(login.EventQueue);
        worldLoginPublication.Publish(acceptedLogin, "session-reconnect");
        window.CompleteReconnect(true, loginStatus);
        StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectReason"));
        _ = playerDirectory.RefreshAsync(manual: false);
        StartRefreshChatMessages();
        StartManualPresence();
        ReconcileRestoredRaidBattleSession(result.Snapshot.ActiveRaidRecovery);
    }

    private static string FormatSessionReconnectBlockReason(SessionReconnectBlockReason reason)
    {
        return reason switch
        {
            SessionReconnectBlockReason.NotInPlayableSession =>
                ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectNotPlaying"),
            SessionReconnectBlockReason.MissingIdentity =>
                ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectMissingIdentity"),
            SessionReconnectBlockReason.SynchronizationBusy =>
                ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectBusy"),
            SessionReconnectBlockReason.AtomicMutationPending =>
                ClashOfRimText.Key("ClashOfRim.SessionExpired.ReconnectAtomicPending"),
            _ => string.Empty
        };
    }

    internal void StartMapServerSession() => _ = sessionStartup.StartAsync(ClientSessionStartKind.Map);
}
