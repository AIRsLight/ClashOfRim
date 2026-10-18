using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientWorldCatalogRuntime { IClientWorldCatalogSession Bind(); }
internal interface IClientWorldCatalogSession
{
    bool IsCurrent { get; }
    void Enqueue(Action action, Action discard);
}
internal interface IClientWorldCatalogPresentation : IClientWorldPreparationPresentation
{
    string Summary(ModWorldConfigurationDto configuration);
    void Status(string message);
}

// A refresh owns the captured identity and world until publication finishes.
// Replacing a read invalidates both its network continuation and queued effects.
internal sealed class ClientWorldCatalog
{
    private readonly ClientRequestTracker<IClientWorldCatalogSession> requests;
    private readonly IClientWorldPreparationTransport transport;
    private readonly IClientWorldCatalogPresentation presentation;
    private readonly ClientWorldConfiguration configuration;
    internal event Action<string?>? CompatibilityAccepted;
    internal event Action<bool>? AdministratorObserved;
    internal event Action<ModWorldConfigurationDto?>? Applied;
    internal bool Busy => requests.Busy;
    internal ClientWorldCatalog(ClientSessionLifecycle session, IClientWorldCatalogRuntime runtime,
        IClientWorldPreparationTransport transport, IClientWorldCatalogPresentation presentation, ClientWorldConfiguration configuration)
    {
        requests = new(session, runtime.Bind, bound => bound.IsCurrent, static () => { });
        this.transport = transport; this.presentation = presentation; this.configuration = configuration;
    }
    internal Task RefreshAsync(string reason, bool generation = false)
        => RefreshAsync(requests.Begin(replace: true)!, reason, generation);

    private async Task RefreshAsync(ClientRequestTracker<IClientWorldCatalogSession>.Request request, string reason, bool generation)
    {
        bool Current() => requests.TryApply(request, static () => { });
        try
        {
            if (!request.Context.IsConfigured || string.IsNullOrWhiteSpace(request.Context.AuthToken))
            { Report(request, () => presentation.Text("ClashOfRim.WorldCatalog.StatusNotConnected")); return; }
            Report(request, () => presentation.Text("ClashOfRim.WorldCatalog.StatusRefreshing", ("REASON", reason)));
            await ClientSessionWork.Run(async () =>
            {
                if (!Current()) return;
                var prepared = Accept(request, await transport.PrepareAsync(request.Context, false, request.Scope.Owner.Token).ConfigureAwait(false), response => response.Result);
                if (prepared is null || !Current()) return;
                var world = prepared.WorldConfigured ? prepared.WorldConfiguration : null;
                // Collection contents cannot prove that a generation baseline was included.
                // Full refreshes explicitly request it, even if preparation returned a catalog.
                if (prepared.WorldConfigured && (generation || world is null))
                {
                    var read = Accept(request, await transport.ReadAsync(request.Context, generation, request.Scope.Owner.Token).ConfigureAwait(false), response => response.Result);
                    if (read is null || !Current()) return;
                    world = read.WorldConfiguration;
                    if (world is null)
                    {
                        Report(request, () => presentation.Text("ClashOfRim.WorldCatalog.StatusFailed", ("CODE", "WorldConfigurationFailed"),
                            ("MESSAGE", presentation.Text("ClashOfRim.WorldConfiguredButMissingConfiguration"))));
                        return;
                    }
                }
                bool published = false;
                await ClientGamePublication.EnqueueAsync(request.Scope.Boundary.Enqueue, request.Scope.Owner.Token,
                    action => requests.TryApply(request, action), () =>
                    {
                        if (published) return;
                        published = true;
                        bool Step(Action action) => requests.TryApply(request, action) && Current();
                        if (!Step(() => CompatibilityAccepted?.Invoke(prepared.ServerCompatibilityManifestJson))) return;
                        if (!Step(() => AdministratorObserved?.Invoke(prepared.IsAdministrator))) return;
                        if (world is not null && !(generation
                            ? configuration.Apply(world, current: Current)
                            : configuration.ApplyCatalog(world, applyStoryteller: true, current: Current))) return;
                        if (!Step(() => Applied?.Invoke(world))) return;
                        if (world is null) Report(request, () => presentation.Text("ClashOfRim.WorldCatalog.StatusNoConfiguration"));
                        else
                        {
                            string summary = "";
                            if (!Step(() => summary = presentation.Summary(world))) return;
                            Report(request, () => presentation.Text("ClashOfRim.WorldCatalog.StatusSucceeded", ("SUMMARY", summary), ("SITES", world.PlayerColonySites.Count)));
                        }
                    }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(request, () => presentation.Text("ClashOfRim.WorldCatalog.StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message)),
                "World configuration refresh exception: " + ex);
        }
        finally { requests.End(request); }
    }
    private T? Accept<T>(ClientRequestTracker<IClientWorldCatalogSession>.Request request,
        ClashOfRimClientNetworkResult<T> result, Func<T, ModProtocolResponseDto?> responseResult) where T : class
    {
        if (!requests.TryApply(request, static () => { })) return null;
        if (!result.Success || result.Response is null)
        {
            Report(request, () => presentation.Text("ClashOfRim.WorldCatalog.StatusFailed", ("CODE", result.ErrorCode ?? ""), ("MESSAGE", result.Message ?? "")),
                result.ErrorCode == nameof(TaskCanceledException) ? null : "World configuration refresh failed: " + result.ErrorCode + " " + result.Message);
            return null;
        }
        var response = ClientProtocolJson.Copy(result.Response);
        var protocol = responseResult(response);
        if (protocol?.Accepted == true) return response;
        Report(request, () => presentation.Text("ClashOfRim.WorldCatalog.StatusRejected", ("CODE", protocol?.ErrorCode.ToString() ?? ""), ("MESSAGE", protocol?.Message ?? "")),
            "World configuration refresh rejected: " + protocol?.ErrorCode + " " + protocol?.Message);
        return null;
    }
    private void Report(ClientRequestTracker<IClientWorldCatalogSession>.Request request, Func<string> format, string? diagnostic = null)
    {
        string message = "";
        if (!requests.TryApply(request, () => message = format())) return;
        // Localization/summary providers can reenter refresh or replace the world.
        requests.TryApply(request, () => presentation.Status(message));
        if (diagnostic is not null) requests.TryApply(request, () => presentation.Diagnose(diagnostic));
    }
}
