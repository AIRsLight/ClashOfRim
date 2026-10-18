using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientWorldFeatureCatalogTransport
{
    Task<ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>> SubmitNamesAsync(
        ClashOfRimClientNetworkContext context, string language, string world, IReadOnlyList<ModWorldFeatureDto> features, CancellationToken token);
}
internal sealed record ClientWorldFeatureNames(string Language, IReadOnlyList<ModWorldFeatureDto> Features);

// Only accepted receipts suppress later submissions. In-flight keys and accepted
// catalogs belong to one session; an old completion cannot remove a new request.
internal sealed class ClientWorldFeatureCatalogs
{
    private readonly ClientRequestTracker requests;
    private readonly IClientWorldFeatureCatalogTransport transport;
    private readonly HashSet<Key> accepted = new();
    private readonly Dictionary<Key, object> pending = new();
    internal event Action<string>? Diagnostic;
    internal ClientWorldFeatureCatalogs(ClientSessionLifecycle session, IClientWorldFeatureCatalogTransport transport)
    { this.transport = transport; requests = new(session, () => { accepted.Clear(); pending.Clear(); }); }
    internal async Task SubmitAsync(string worldId, ClientWorldFeatureNames names)
    {
        if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(names.Language) || names.Features.Count == 0) return;
        var scope = requests.Capture();
        if (!scope.Context.IsConfigured) return;
        string world = worldId.Trim(), language = names.Language.Trim();
        var key = new Key(world, language.ToUpperInvariant()); var ticket = new object();
        bool started = false;
        requests.TryApply(scope, () => { if (!accepted.Contains(key) && !pending.ContainsKey(key)) { pending.Add(key, ticket); started = true; } });
        if (!started) return;
        try
        {
            var features = new List<ModWorldFeatureDto>();
            foreach (var feature in names.Features) features.Add(ClientProtocolJson.Copy(feature));
            var result = await transport.SubmitNamesAsync(scope.Context, language, world, features, scope.Owner.Token).ConfigureAwait(false);
            requests.TryApply(scope, () =>
            {
                if (result.Success && result.Response?.Result?.Accepted == true && result.Response.Accepted) accepted.Add(key);
                else Diagnostic?.Invoke("[ClashOfRim] World feature name submission failed: " + (result.Message ?? result.Response?.Result?.Message ?? result.ErrorCode ?? "unknown"));
            });
        }
        catch (Exception ex) { requests.TryApply(scope, () => Diagnostic?.Invoke("[ClashOfRim] World feature name submission failed: " + ex.Message)); }
        finally { requests.TryApply(scope, () => { if (pending.TryGetValue(key, out var value) && ReferenceEquals(value, ticket)) pending.Remove(key); }); }
    }
    private sealed record Key(string World, string Language);
}
