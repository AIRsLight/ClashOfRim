using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.World;

// Setup intentionally survives vanilla's creation of a new Game, but never a new
// multiplayer identity. Network preparation itself also binds the source Game.
internal sealed class ClientWorldSetupState
{
    private readonly ClientRequestTracker requests;
    private bool initial, languageAccepted;
    private ModWorldConfigurationDto? configuration;
    private WorldSubstratePackage? substrate;
    internal ClientWorldSetupState(ClientSessionLifecycle session)
        => requests = new(session, () => { initial = languageAccepted = false; configuration = null; substrate = null; });
    internal bool Pending => requests.Read(() => initial || configuration is not null);
    internal bool HasConfiguration => requests.Read(() => configuration is not null);
    internal bool LanguageAccepted { get => requests.Read(() => languageAccepted); set => requests.Read(() => languageAccepted = value); }
    internal ModWorldConfigurationDto? Configuration => requests.Read(() => configuration is null ? null : ClientProtocolJson.Copy(configuration));
    internal void Reset() => requests.Reset();
    internal void Prepare(ModPrepareWorldSessionResponseDto response)
    {
        requests.Read(() =>
        {
            bool acceptedLanguage = languageAccepted;
            requests.Reset();
            languageAccepted = acceptedLanguage;
            if (response.Result?.Accepted != true || response.HasExistingColony) return false;
            initial = response.RequiresInitialWorldConfiguration;
            configuration = response.WorldConfigured && response.WorldConfiguration is not null ? ClientProtocolJson.Copy(response.WorldConfiguration) : null;
            return true;
        });
    }
    internal Ticket? CaptureConfiguration() => requests.Read(() => configuration is null ? null : new Ticket(requests.Capture(), ClientProtocolJson.Copy(configuration)));
    internal bool IsCurrent(Ticket ticket) => requests.TryApply(ticket.Scope, static () => { });
    internal Ticket? CaptureGeneration() => requests.Read(() => substrate is null ? CaptureConfiguration() : null);
    internal bool AttachSubstrate(Ticket ticket, WorldSubstratePackage value)
    {
        bool attached = false;
        requests.TryApply(ticket.Scope, () =>
        {
            if (configuration is null || substrate is not null) return;
            substrate = Copy(value); attached = true;
        });
        return attached;
    }
    internal bool RejectGeneration(Ticket ticket)
    {
        bool rejected = false;
        requests.TryApply(ticket.Scope, () =>
        {
            if (configuration is null || substrate is not null) return;
            bool keepLanguage = languageAccepted;
            requests.Reset(); languageAccepted = keepLanguage; rejected = true;
        });
        return rejected;
    }
    internal Generation? TakeGeneration() => requests.Read(() =>
    {
        if (configuration is null || substrate is null) return null;
        var capturedConfiguration = configuration; var capturedSubstrate = substrate;
        configuration = null; substrate = null;
        // Retire pending download tickets as ownership moves into generation.
        bool keepInitial = initial, keepLanguage = languageAccepted;
        requests.Reset(); initial = keepInitial; languageAccepted = keepLanguage;
        return new Generation(requests.Capture(), capturedConfiguration, capturedSubstrate);
    });
    internal bool IsCurrent(Generation generation) => requests.TryApply(generation.Scope, static () => { });
    internal bool RestoreGeneration(Generation generation)
    {
        bool restored = false;
        requests.TryApply(generation.Scope, () =>
        {
            if (configuration is not null || substrate is not null) return;
            configuration = generation.Configuration; substrate = generation.Substrate; restored = true;
        });
        return restored;
    }
    internal ClientRequestTracker.Scope? TakeInitial() => requests.Read(() =>
    {
        if (!initial) return null;
        initial = false; return requests.Capture();
    });
    internal void RestoreInitial(ClientRequestTracker.Scope ticket) => requests.TryApply(ticket, () => initial = true);
    private static WorldSubstratePackage Copy(WorldSubstratePackage source) => new(source.PersistentRandomValue, source.GridXml,
        source.FeaturesXml, source.LandmarksXml, source.TileGeometryPayload);
    internal sealed record Ticket(ClientRequestTracker.Scope Scope, ModWorldConfigurationDto Configuration)
    { internal ClashOfRimClientNetworkContext Context => Scope.Context; }
    internal sealed record Generation(ClientRequestTracker.Scope Scope, ModWorldConfigurationDto Configuration, WorldSubstratePackage Substrate);
}
