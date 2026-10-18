using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientWorldBaselineRuntime { IClientWorldBaselineSession Bind(); }
internal interface IClientWorldBaselineSession { bool IsCurrent { get; } }
internal interface IClientWorldBaselineTransport
{
    Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ModWorldConfigurationDto configuration, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModUploadWorldSubstrateResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string worldId, byte[] payload, CancellationToken token);
}

// A baseline write belongs to the session and world that supplied its data.
// Metadata acceptance is only an intermediate result: the detached substrate
// must be accepted too before callers can publish success.
internal sealed class ClientWorldBaselineSubmission
{
    private readonly ClientRequestTracker<IClientWorldBaselineSession> requests;
    private readonly IClientWorldBaselineTransport transport;
    private readonly ClientSessionLifecycle session;
    private Prepared? latest;
    internal ClientWorldBaselineSubmission(ClientSessionLifecycle session, IClientWorldBaselineRuntime runtime, IClientWorldBaselineTransport transport)
    { requests = new(session, runtime.Bind, bound => bound.IsCurrent, () => latest = null); this.transport = transport; this.session = session; }

    internal Captured Capture() => requests.Read(() => new Captured(requests.Capture(), session.ReadContext()));

    internal Prepared? Prepare(Captured capture, ModWorldConfigurationDto configuration, byte[] payload)
    {
        if (!requests.TryApply(capture.Scope, static () => { })) return null;
        if (!capture.Context.IsConfigured || string.IsNullOrWhiteSpace(configuration.WorldConfigurationId))
            throw new InvalidOperationException("World baseline identity is missing.");
        if (payload.Length == 0 || configuration.TileGeometry?.Layers.Any(layer => layer.TileCenters.Count > 0) != true)
            throw new InvalidOperationException("World baseline substrate is missing.");
        // Exclude the potentially very large geometry before serialization, without
        // temporarily mutating the caller's DTO or maintaining a second field list.
        var metadata = ClientProtocolJson.Copy(configuration.MetadataView());
        metadata.ConfiguredByUserId = capture.Context.UserId;
        metadata.ConfiguredByColonyId = capture.Context.ColonyId;
        byte[] bytes = payload.ToArray();
        Prepared? prepared = null;
        requests.TryApply(capture.Scope, () =>
        {
            var request = requests.Begin();
            if (request is null) return;
            prepared = new Prepared(capture, request, metadata, bytes);
            latest = prepared;
        });
        return prepared;
    }

    internal bool TryApply(Prepared prepared, Action action)
    {
        bool applied = false;
        requests.TryApply(prepared.Capture.Scope, () =>
        {
            if (!ReferenceEquals(latest, prepared)) return;
            action(); applied = true;
        });
        return applied;
    }
    internal bool IsCurrent(Prepared prepared) => TryApply(prepared, static () => { });
    internal void Discard(Prepared prepared) => TryApply(prepared, () =>
    {
        requests.End(prepared.Request);
        prepared.Input = null;
        latest = null;
    });
    internal bool Complete(Prepared prepared, Action action)
    {
        bool completed = false;
        TryApply(prepared, () =>
        {
            if (!prepared.Succeeded || prepared.Published) return;
            prepared.Published = true;
            completed = true;
            action();
        });
        return completed;
    }

    internal async Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>?> SubmitAsync(Prepared prepared)
    {
        var request = prepared.Request;
        (ModWorldConfigurationDto Metadata, byte[] Payload)? input = null;
        requests.TryApply(request, () =>
        {
            input = prepared.Input;
            prepared.Input = null;
        });
        if (input is null) return null;
        // Retain only the receipt after sending, not a whole world's byte package.
        var (metadata, payload) = input.Value;
        bool Current() => requests.TryApply(request, static () => { });
        try
        {
            if (!Current()) return null;
            var result = await transport.SubmitAsync(prepared.Context, metadata, request.Scope.Owner.Token).ConfigureAwait(false);
            if (!Current()) return null;
            if (!result.Success) return result;
            if (result.Response?.Result is null) return Failure("InvalidWorldBaselineResponse", "World baseline response is missing its result.");
            // The upload must use the accepted identity even if a transport later
            // reuses or modifies its response object.
            result = ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>.Ok(ClientProtocolJson.Copy(result.Response));
            if (!result.Response!.Result!.Accepted) return result;
            string worldId = result.Response.WorldConfiguration?.WorldConfigurationId ?? metadata.WorldConfigurationId;
            if (string.IsNullOrWhiteSpace(worldId)) return Failure("MissingWorldConfigurationId", "Accepted world baseline has no world configuration id.");
            if (!Current()) return null;
            var substrate = await transport.UploadAsync(prepared.Context, worldId, payload, request.Scope.Owner.Token).ConfigureAwait(false);
            if (!Current()) return null;
            if (!substrate.Success || substrate.Response?.Result?.Accepted != true)
                return Failure("WorldSubstrateFailed", substrate.Message ?? substrate.Response?.Result?.Message
                    ?? substrate.ErrorCode ?? substrate.Response?.Result?.ErrorCode.ToString() ?? "World substrate upload was not accepted.");
            return requests.TryApply(request, () => prepared.Succeeded = true) ? result : null;
        }
        catch (Exception) when (!Current()) { return null; }
        finally { requests.End(request); }
    }
    private static ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto> Failure(string code, string message)
        => ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>.Failed(code, message);

    internal sealed record Captured(ClientRequestTracker<IClientWorldBaselineSession>.Scope Scope, ClashOfRimClientNetworkContext Context);

    internal sealed class Prepared
    {
        internal readonly Captured Capture;
        internal readonly ClientRequestTracker<IClientWorldBaselineSession>.Request Request;
        internal (ModWorldConfigurationDto Metadata, byte[] Payload)? Input;
        internal bool Succeeded, Published;
        internal ClashOfRimClientNetworkContext Context => Capture.Context;
        internal Prepared(Captured capture, ClientRequestTracker<IClientWorldBaselineSession>.Request request, ModWorldConfigurationDto metadata, byte[] payload)
        { Capture = capture; Request = request; Input = (metadata, payload); }
    }
}
