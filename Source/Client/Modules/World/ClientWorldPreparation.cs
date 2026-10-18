using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientWorldPreparationTransport
{
    Task<ClashOfRimClientNetworkResult<ModServerHelloResponseDto>> HelloAsync(ClashOfRimClientNetworkContext context, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, bool createAccount, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, bool generation, CancellationToken token);
}
internal interface IClientWorldPreparationPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    void Diagnose(string message);
}

// Produces a prepared response; UI decisions, world generation and snapshot reload
// consume it only through Complete, which rechecks the original request owner.
internal sealed class ClientWorldPreparation
{
    private readonly ClientGameRequestTracker requests;
    private readonly IClientWorldPreparationTransport transport;
    private readonly IClientWorldPreparationPresentation presentation;
    internal ClientWorldPreparation(ClientSessionLifecycle session, IClientSnapshotGame game,
        IClientWorldPreparationTransport transport, IClientWorldPreparationPresentation presentation)
    { requests = new(session, game, static () => { }); this.transport = transport; this.presentation = presentation; }
    internal ClientGameRequestTracker.Request Begin() => requests.Begin(replace: true)!;
    internal async Task<Prepared?> PrepareAsync(bool createAccount, ClientGameRequestTracker.Request? captured = null)
    {
        var request = captured ?? Begin();
        Prepared? prepared = null;
        bool Current() => requests.TryApply(request, static () => { });
        try
        {
            if (!Current()) return null;
            var hello = await transport.HelloAsync(request.Context, request.Scope.Owner.Token).ConfigureAwait(false);
            if (!Current()) return null;
            string? helloFailure = ValidateHello(hello);
            if (helloFailure is not null)
            {
                if (Current()) prepared = new(request, null, helloFailure);
                return prepared;
            }
            if (!Current()) return null;
            var result = await transport.PrepareAsync(request.Context, createAccount, request.Scope.Owner.Token).ConfigureAwait(false);
            if (!Current()) return null;
            // The transport's response belongs to this request from this point on.
            if (result.Response is not null) result = ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Ok(ClientProtocolJson.Copy(result.Response));
            if (result.Success && result.Response is { } response && response.Result?.Accepted == true
                && response.WorldConfigured && response.WorldConfiguration is null)
            {
                var context = request.Context;
                var assigned = new ClashOfRimClientNetworkContext(context.ServerBaseUrl, context.UserId,
                    response.AssignedColonyId ?? context.ColonyId, context.CurrentSnapshotId, context.SteamAuthTicket, context.OfflinePassword, context.AuthToken);
                var read = await transport.ReadAsync(assigned, !response.HasExistingColony, request.Scope.Owner.Token).ConfigureAwait(false);
                if (!Current()) return null;
                if (!read.Success || read.Response is null)
                {
                    if (!response.HasExistingColony)
                        result = ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Failed(read.ErrorCode ?? "WorldConfigurationFailed",
                            read.Message ?? presentation.Text("ClashOfRim.WorldConfiguredButMissingConfiguration"));
                    else presentation.Diagnose("Existing-colony world configuration refresh failed during entry: " + read.ErrorCode + " " + read.Message);
                }
                else if (read.Response.Result?.Accepted != true)
                {
                    if (!response.HasExistingColony) response.Result = read.Response.Result is null ? null : ClientProtocolJson.Copy(read.Response.Result);
                    else presentation.Diagnose("Existing-colony world configuration refresh rejected during entry: " + read.Response.Result?.ErrorCode + " " + read.Response.Result?.Message);
                }
                else response.WorldConfiguration = read.Response.WorldConfiguration is null ? null : ClientProtocolJson.Copy(read.Response.WorldConfiguration);
            }
            if (Current()) prepared = new(request, result, null);
            return prepared;
        }
        catch (Exception ex)
        {
            if (!Current()) return null;
            string text = presentation.Text("ClashOfRim.ServerEntry.StatusFlowException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
            if (Current()) prepared = new(request, null, text);
            return prepared;
        }
        finally { if (prepared is null) requests.End(request); }
    }
    internal bool Complete(Prepared prepared, Action<Prepared> publish)
    {
        return requests.TryApply(prepared.Request, () =>
        {
            // Consume before invoking the UI; a reentrant callback cannot publish twice.
            requests.End(prepared.Request);
            publish(prepared);
        });
    }
    internal void Discard(Prepared prepared) => Discard(prepared.Request);
    internal void Discard(ClientGameRequestTracker.Request request) => requests.End(request);
    private string? ValidateHello(ClashOfRimClientNetworkResult<ModServerHelloResponseDto> result)
    {
        if (!result.Success || result.Response is null)
            return presentation.Text("ClashOfRim.ServerEntry.ServerHelloFailed", ("CODE", result.ErrorCode ?? "ServerHelloFailed"),
                ("MESSAGE", result.Message ?? presentation.Text("ClashOfRim.UnknownReason")));
        var response = result.Response;
        if (response.Result?.Accepted != true) return response.Result?.Message ?? presentation.Text("ClashOfRim.ServerEntry.ServerHelloRejected");
        if (response.ProtocolMajor == ClashOfRimVersion.ProtocolMajor && response.MinimumSupportedProtocolMajor == ClashOfRimVersion.ProtocolMajor
            && ClashOfRimVersion.ProtocolMinor >= response.MinimumSupportedProtocolMinor && ClashOfRimVersion.ProtocolMinor <= response.ProtocolMinor
            && string.Equals(response.ProtocolVersion, ClashOfRimVersion.ProtocolVersion, StringComparison.Ordinal)) return null;
        return presentation.Text("ClashOfRim.ServerEntry.ProtocolMismatch", ("CLIENT", $"{ClashOfRimVersion.ProductVersion} / {ClashOfRimVersion.ProtocolDisplayVersion}"),
            ("SERVER", $"{response.ProductVersion} / {response.ProtocolMajor}.{response.ProtocolMinor}"));
    }
    internal sealed record Prepared(ClientGameRequestTracker.Request Request,
        ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>? Result, string? Failure);
}
