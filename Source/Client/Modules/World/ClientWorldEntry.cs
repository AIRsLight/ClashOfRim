using AIRsLight.ClashOfRim.Modules.Compatibility;
using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.World;

// Owns result decisions and their ordering. Rendering and accepted-domain projections
// are effects; none may decide whether an entry can publish identity or start loading.
internal sealed class ClientWorldEntry
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientGameRequestTracker requests;
    private readonly ClientWorldSetupState setup;
    private readonly IClientWorldEntryStore store;
    private readonly IClientWorldSubstrateTransport transport;
    private readonly IClientWorldEntryPresentation view;
    private readonly IClientSnapshotReload reload;
    internal ClientWorldEntry(ClientSessionLifecycle session, IClientSnapshotGame game, ClientWorldSetupState setup,
        IClientWorldEntryStore store, IClientWorldSubstrateTransport transport, IClientWorldEntryPresentation view, IClientSnapshotReload reload)
    {
        this.session = session; this.setup = setup; this.store = store; this.transport = transport; this.view = view; this.reload = reload;
        requests = new(session, game, static () => { });
    }
    internal event Action<ModPrepareWorldSessionResponseDto>? Accepted;
    internal Task Handle(ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>? result, string? failure,
        ClientSynchronizationCoordinator.Lease operation)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!operation.IsCurrent) return Task.CompletedTask;
        requests.Reset();
        var request = requests.Begin()!;
        if (failure is not null) { Fail(request, failure); return Task.CompletedTask; }
        if (result?.Success != true || result.Response is null)
        {
            Fail(request, view.Text("ClashOfRim.WorldSessionRequestFailed", ("CODE", result?.ErrorCode), ("MESSAGE", result?.Message)));
            return Task.CompletedTask;
        }
        return Decide(request, ClientProtocolJson.Copy(result.Response), operation, setup.LanguageAccepted);
    }
    private Task Decide(ClientGameRequestTracker.Request request, ModPrepareWorldSessionResponseDto response,
        ClientSynchronizationCoordinator.Lease operation, bool languageAccepted)
    {
        if (!Current(request) || !operation.IsCurrent) return Task.CompletedTask;
        var warning = ClientCompatibilityWarning.FromPreparation(response);
        if (response.Result?.Accepted != true)
        {
            if (response.Result?.ErrorCode == (int)ProtocolErrorCode.AccountNotFound)
            {
                view.CloseProgress();
                if (Current(request)) view.ConfirmRegistration(request.Context.UserId, () => requests.TryApply(request, () =>
                {
                    requests.End(request);
                    view.Register(request.Context.ServerBaseUrl, request.Context.UserId);
                }));
            }
            else
            {
                Fail(request, view.Text("ClashOfRim.WorldSessionRejected", ("REASON", response.Result?.Message ?? view.Text("ClashOfRim.UnknownReason"))));
                ShowCompatibility(request.Scope, warning, response.WorldConfiguration?.GameLanguage, languageAccepted);
            }
            return Task.CompletedTask;
        }
        string? invalid = !response.WorldConfigured && !response.IsAdministrator
            ? response.Result.Message ?? view.Text("ClashOfRim.WaitingFirstAdminWorldConfiguration")
            : response.HasExistingColony && string.IsNullOrWhiteSpace(response.LatestSnapshotId)
                ? view.Text("ClashOfRim.ExistingColonyMissingSnapshot")
                : response.WorldConfigured && !response.HasExistingColony && string.IsNullOrWhiteSpace(response.WorldConfiguration?.WorldConfigurationId)
                    ? view.Text("ClashOfRim.WorldConfiguredButMissingConfiguration") : null;
        if (invalid is not null) { Fail(request, invalid); return Task.CompletedTask; }
        if (!Current(request)) return Task.CompletedTask;
        if (!languageAccepted && CompatibilityLanguageMismatchPolicy.CanContinue(warning))
        {
            view.CloseProgress();
            if (!Current(request)) return Task.CompletedTask;
            operation.QueueContinuation((accept, cancel) => view.Compatibility(ClientProtocolJson.Copy(warning), response.WorldConfiguration?.GameLanguage,
                false, () => Current(request), accept, cancel), next =>
                requests.TryApply(request, () => { _ = Decide(request, response, next, true); }), completed: () => requests.End(request));
            return Task.CompletedTask;
        }
        return Publish(request, response, warning, operation, languageAccepted);
    }
    private Task Publish(ClientGameRequestTracker.Request request, ModPrepareWorldSessionResponseDto response,
        ModLoginResponseDto warning, ClientSynchronizationCoordinator.Lease operation, bool languageAccepted)
    {
        var context = request.Context;
        string colony = string.IsNullOrWhiteSpace(response.AssignedColonyId) ? context.ColonyId : response.AssignedColonyId!;
        string snapshot = !string.IsNullOrWhiteSpace(response.LatestSnapshotId) ? response.LatestSnapshotId!
            : colony == context.ColonyId ? context.CurrentSnapshotId ?? "" : "";
        string message = response.HasExistingColony
            ? view.Text("ClashOfRim.ExistingColonyLoadFlow", ("USER", context.UserId), ("COLONY", colony), ("SNAPSHOT", snapshot))
            : view.Text(response.RequiresInitialWorldConfiguration ? "ClashOfRim.FirstAdminWorldConfigurationFlow" : "ClashOfRim.JoinExistingWorldFlow",
                ("USER", context.UserId), ("COLONY", colony));
        if (!Current(request) || !operation.IsCurrent) return Task.CompletedTask;
        try { store.Publish(colony, snapshot); }
        catch (Exception ex)
        {
            Fail(request, view.Text("ClashOfRim.ServerEntry.StatusFlowException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message)));
            return Task.CompletedTask;
        }
        var expected = new ClashOfRimClientNetworkContext(context.ServerBaseUrl, context.UserId, colony, snapshot, context.SteamAuthTicket, context.OfflinePassword, context.AuthToken);
        // Only this deliberate colony transition can rebind ownership. A persistence
        // callback that changes account/login/Game must not be adopted as this entry.
        if (!session.Owns(request.Scope.Owner, expected, false) || !request.Scope.Boundary.IsCurrent) return Task.CompletedTask;
        requests.Reset(); request = requests.Begin()!;
        Accepted?.Invoke(ClientProtocolJson.Copy(response));
        if (!Current(request) || !operation.IsCurrent) return Task.CompletedTask;
        ShowCompatibility(request.Scope, warning, response.WorldConfiguration?.GameLanguage, languageAccepted);
        if (!Current(request)) return Task.CompletedTask;
        void PrepareSetup() { setup.LanguageAccepted = languageAccepted; setup.Prepare(response); }
        if (response.HasExistingColony)
        {
            view.Progress(message, 0.05f);
            if (!Current(request) || !operation.IsCurrent) return Task.CompletedTask;
            PrepareSetup();
            requests.End(request);
            return reload.StartAsync(new(snapshot, ClientSnapshotReloadPurpose.Entry, response.WorldConfiguration), operation);
        }
        if (!response.WorldConfigured)
        {
            PrepareSetup();
            requests.End(request); view.Scenario(message); return Task.CompletedTask;
        }
        view.Progress(message, 0.35f);
        if (!Current(request) || !operation.IsCurrent) return Task.CompletedTask;
        PrepareSetup();
        var pending = setup.CaptureGeneration()!;
        return operation.RunContinuation(next => Download(request, pending, next));
    }
    private async Task Download(ClientGameRequestTracker.Request request, ClientWorldSetupState.Ticket pending, ClientSynchronizationCoordinator.Lease operation)
    {
        bool queued = false;
        void Finish()
        {
            // Discarded main-thread callbacks and source-Game replacement must not
            // leave a configuration waiting forever for an abandoned download.
            setup.RejectGeneration(pending);
            requests.End(request);
        }
        try
        {
            if (!Current(request)) return;
            var result = await transport.DownloadAsync(pending.Context, pending.Configuration.WorldConfigurationId, request.Scope.Owner.Token).ConfigureAwait(false);
            if (!Current(request) || !operation.IsCurrent) return;
            string? failure = null; WorldSubstratePackage? package = null;
            bool decoded = result.Success && result.Response is not null && WorldSubstratePackageCodec.TryDecode(result.Response, out package, out failure) && package is not null;
            string? message = decoded ? null : view.Text("ClashOfRim.WorldSessionRequestFailed", ("CODE", "WorldSubstrate"), ("MESSAGE", result.Message ?? result.ErrorCode ?? failure ?? "Unknown"));
            if (!Current(request) || !operation.IsCurrent) return;
            operation.QueueContinuation(view.Enqueue, _ => requests.TryApply(request, () =>
            {
                if (decoded)
                {
                    if (setup.AttachSubstrate(pending, package!)) { requests.End(request); view.Scenario(null); }
                }
                else if (setup.RejectGeneration(pending)) Fail(request, message!);
            }), completed: Finish);
            queued = true;
        }
        catch (Exception ex)
        {
            if (!Current(request) || !operation.IsCurrent) return;
            string message = view.Text("ClashOfRim.WorldSessionRequestFailed", ("CODE", ex.GetType().Name), ("MESSAGE", ex.Message));
            if (!Current(request) || !operation.IsCurrent) return;
            operation.QueueContinuation(view.Enqueue, _ => requests.TryApply(request, () =>
            {
                if (setup.RejectGeneration(pending)) Fail(request, message);
            }), completed: Finish);
            queued = true;
        }
        finally { if (!queued) Finish(); }
    }
    private void ShowCompatibility(ClientGameRequestTracker.Scope scope, ModLoginResponseDto warning, string? language, bool accepted)
    {
        if (requests.TryApply(scope, static () => { }))
            view.Compatibility(ClientProtocolJson.Copy(warning), language, accepted, () => requests.TryApply(scope, static () => { }));
    }
    private bool Current(ClientGameRequestTracker.Request request) => requests.TryApply(request, static () => { });
    private void Fail(ClientGameRequestTracker.Request request, string message)
        => requests.TryApply(request, () => { requests.End(request); view.Failure(message); });
}
