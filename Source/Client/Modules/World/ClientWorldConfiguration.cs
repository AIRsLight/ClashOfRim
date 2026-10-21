using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientWorldIdentity { string WorldId { get; } }
internal interface IClientWorldConfigurationStore { void Remember(string worldId); }
internal interface IClientWorldConfigurationRuntime { IClientWorldConfigurationSession Bind(); }
internal interface IClientWorldConfigurationSession
{
    bool IsCurrent { get; }
    ClientWorldFeatureNames? ApplyFeatures(ModWorldConfigurationDto configuration);
    void ApplyFactions(ModWorldConfigurationDto configuration);
    void ApplyRoads(ModWorldConfigurationDto configuration);
    void ApplyExtensions(ModWorldConfigurationDto configuration, string userId, bool worldState);
    void ApplyObjectLabels(ModWorldConfigurationDto configuration);
}

// World generation, administrator refresh and catalog-only refresh share one
// application order and session/world boundary. Runtime ports perform engine work.
internal sealed class ClientWorldConfiguration
{
    private readonly ClientRequestTracker<IClientWorldConfigurationSession> requests;
    private readonly IClientWorldConfigurationStore store;
    private readonly ClientStorytellerConfiguration storyteller;
    private readonly ClientWorldFeatureCatalogs catalogs;
    internal ClientWorldConfiguration(ClientSessionLifecycle session, IClientWorldConfigurationRuntime runtime,
        IClientWorldConfigurationStore store, ClientStorytellerConfiguration storyteller, ClientWorldFeatureCatalogs catalogs)
    { requests = new(session, runtime.Bind, bound => bound.IsCurrent, static () => { }); this.store = store; this.storyteller = storyteller; this.catalogs = catalogs; }
    internal void Remember(ModWorldConfigurationDto? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration?.WorldConfigurationId)) return;
        var scope = requests.Capture(); requests.TryApply(scope, () => store.Remember(configuration!.WorldConfigurationId.Trim()));
    }
    internal bool Apply(ModWorldConfigurationDto configuration, bool worldState = false, bool features = true, ClientWorldFeatureNames? generatedNames = null, Func<bool>? current = null)
        => Project(ClientProtocolJson.Copy(configuration), true, worldState, features, true, generatedNames, current);
    internal bool ApplyCatalog(ModWorldConfigurationDto? configuration, bool applyStoryteller = false, Func<bool>? current = null)
        => configuration is not null && Project(ClientProtocolJson.Copy(configuration), false, false, false, applyStoryteller, null, current);
    // Publication has already isolated this DTO and its site/marker projection.
    internal bool ApplyOwned(ModWorldConfigurationDto configuration, ClientWorldProjection projection, Func<bool> current)
        => Project(configuration, projection == ClientWorldProjection.Full, false, true,
            projection is ClientWorldProjection.Full or ClientWorldProjection.Storyteller, null, current);
    private bool Project(ModWorldConfigurationDto configuration, bool full, bool worldState, bool features, bool applyStoryteller, ClientWorldFeatureNames? names, Func<bool>? current)
    {
        var scope = requests.Capture(); var runtime = scope.Boundary;
        bool Step(Action action)
        {
            bool applied = false;
            requests.TryApply(scope, () => { if (current?.Invoke() != false) { action(); applied = true; } });
            return applied && requests.TryApply(scope, static () => { }) && current?.Invoke() != false;
        }
        if (!Step(() => Remember(configuration))) return false;
        if (full)
        {
            if (features && !Step(() => names = runtime.ApplyFeatures(configuration))) return false;
            if (!Step(() => runtime.ApplyFactions(configuration)) || !Step(() => runtime.ApplyRoads(configuration))) return false;
        }
        if (!Step(() => runtime.ApplyExtensions(configuration, scope.Context.UserId, worldState))) return false;
        if (full && !Step(() => runtime.ApplyObjectLabels(configuration))) return false;
        if (applyStoryteller && !Step(() => storyteller.ApplyRuntime(configuration, current))) return false;
        return names is null || Step(() => _ = catalogs.SubmitAsync(configuration.WorldConfigurationId, names));
    }
}
