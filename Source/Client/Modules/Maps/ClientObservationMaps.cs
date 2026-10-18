using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.RemoteMaps;

namespace AIRsLight.ClashOfRim.Modules.Maps;

internal sealed class ClientObservationMaps
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly IClientSnapshotGame game;
    private readonly IClientObservationTransport transport;
    private readonly IClientObservationRuntime runtime;
    private readonly IClientObservationPresentation presentation;

    internal ClientObservationMaps(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
        IClientSnapshotGame game, IClientObservationTransport transport, IClientObservationRuntime runtime,
        IClientObservationPresentation presentation)
    { this.session = session; this.synchronization = synchronization; this.game = game;
        this.transport = transport; this.runtime = runtime; this.presentation = presentation; }

    internal Task? OpenAsync(ModWorldMapMarkerDto target, string mode)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var context = session.ReadContext();
        if (!context.IsConfigured) { presentation.Reject("ClashOfRim.Observation.StatusNotConfigured"); return null; }
        if (string.IsNullOrWhiteSpace(target.OwnerUserId) || string.IsNullOrWhiteSpace(target.OwnerColonyId))
        { presentation.Reject("ClashOfRim.Observation.StatusTargetIncomplete"); return null; }
        string scope = ClientRemoteMapModes.Normalize(mode);
        if (scope == ClientRemoteMapModes.RaidObservation && string.IsNullOrWhiteSpace(target.RelatedEventId))
        { presentation.Reject("ClashOfRim.Observation.StatusMissingRaidEvent"); return null; }
        // Marker refreshes and UI edits must not redirect a queued map load.
        var captured = ClientProtocolJson.Deserialize<ModWorldMapMarkerDto>(ClientProtocolJson.Serialize(target), type => type);
        if (!synchronization.TryBeginManual(out var admission))
        { presentation.Reject("ClashOfRim.Sync.StatusBusy"); return null; }
        try
        {
            var attempt = new Attempt(context, session.CaptureOwnership(), game.BindCurrentGame(), admission);
            presentation.Downloading(scope, captured);
            return RunOwnedAsync(attempt, captured, scope);
        }
        catch { admission.Dispose(); throw; }
    }

    private async Task RunOwnedAsync(Attempt attempt, ModWorldMapMarkerDto target, string mode)
    {
        try { await ClientSessionWork.Run(() => RunAsync(attempt, target, mode)).ConfigureAwait(false); }
        finally
        {
            attempt.Admission.Dispose();
            if (!attempt.Adopted && attempt.Credential is { } credential)
                await session.ReleaseSessionAsync(credential.Context, credential.Receipt.SessionId!).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Attempt attempt, ModWorldMapMarkerDto target, string mode)
    {
        bool opening = false;
        try
        {
            if (!Apply(attempt, static () => { })) return;
            if (string.IsNullOrWhiteSpace(attempt.Context.AuthToken))
            {
                var login = await transport.LoginAsync(attempt.Context, attempt.Owner.Token,
                    receipt => attempt.Credential = ClientSessionCredential.Capture(attempt.Context, receipt)).ConfigureAwait(false);
                await Publish(attempt, () =>
                {
                    if (!login.Success || attempt.Credential is not { } credential) { presentation.LoginFailed(login); return; }
                    var accepted = session.PublishLogin(attempt.Owner, attempt.Context, credential.Receipt);
                    if (accepted is null) return;
                    attempt.Adopted = true;
                    // This workflow adopts its own login while retaining the same game and lease.
                    attempt.Owner = accepted.Owner; attempt.Context = accepted.Context;
                }).ConfigureAwait(false);
                if (!attempt.Adopted) return;
            }
            if (!Apply(attempt, static () => { })) return;
            var snapshot = await transport.DownloadAsync(attempt.Context,
                new(target.OwnerUserId, target.OwnerColonyId!, mode == ClientRemoteMapModes.RaidObservation ? target.RelatedEventId : null, mode),
                attempt.Owner.Token).ConfigureAwait(false);
            Task opened = Task.CompletedTask;
            await Publish(attempt, () =>
            {
                if (!ClientRemoteSnapshotDownload.TryGetOpenPayload(snapshot, out var payload, out var failure))
                { presentation.DownloadFailed(failure!); return; }
                var request = new RemoteSessionMapOpenRequest
                {
                    Target = target, Mode = mode,
                    SessionId = $"observation:{attempt.Context.UserId}:{target.OwnerUserId}:{payload!.SnapshotId}:{DateTime.UtcNow.Ticks}",
                    SnapshotId = payload.SnapshotId, RelatedEventId = target.RelatedEventId ?? string.Empty,
                    Package = payload.Package, Payload = payload.Payload,
                    CloseExistingObservationSessions = true, FailureCleanupReason = "remote session map generation failure"
                };
                opening = true;
                presentation.Generating(target);
                presentation.CloseProgress();
                // Queue the engine long event from the game thread, keeping admission
                // until the actual open callback runs or is discarded.
                opened = ClientGamePublication.EnqueueAsync(runtime.EnqueueOpen, attempt.Owner.Token,
                    action => Apply(attempt, action), () =>
                    {
                        try
                        {
                            string? reason = runtime.Open(request);
                            if (reason is not null) presentation.OpenFailed(reason);
                            else presentation.Loaded(mode, target);
                        }
                        catch (Exception ex) { presentation.Exception(ex, opening: true); }
                    });
            }).ConfigureAwait(false);
            await opened.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ClientSessionWork.IsCurrent) { }
        catch (Exception ex)
        { await Publish(attempt, () => presentation.Exception(ex, opening)).ConfigureAwait(false); }
    }

    private Task Publish(Attempt attempt, Action action) => ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue,
        attempt.Owner.Token, run => Apply(attempt, run), action);
    private bool Apply(Attempt attempt, Action action)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() =>
        {
            if (!attempt.Admission.IsCurrent || !attempt.Game.IsCurrent || !session.Owns(attempt.Owner, attempt.Context, false)) return;
            action(); applied = true;
        });
        return applied;
    }
    private sealed class Attempt
    {
        internal Attempt(ClashOfRimClientNetworkContext context, ClientSessionLifecycle.Ownership owner,
            IClientSnapshotGameSession game, ClientSynchronizationCoordinator.Lease admission)
        { Context = context; Owner = owner; Game = game; Admission = admission; }
        internal ClientSessionCredential? Credential;
        internal bool Adopted;
        internal ClashOfRimClientNetworkContext Context;
        internal ClientSessionLifecycle.Ownership Owner;
        internal readonly IClientSnapshotGameSession Game;
        internal readonly ClientSynchronizationCoordinator.Lease Admission;
    }
}
