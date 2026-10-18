using System;
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
    internal ClientRequestTracker.Request CaptureDecision() => requests.Begin(replace: true)!;
    internal bool Accept(ClientRequestTracker.Request decision, Action action)
        => requests.TryApply(decision, () =>
        {
            requests.End(decision);
            action();
        });
    internal Ticket? CaptureGeneration() => requests.Read(() => configuration is null || substrate is not null ? null : new Ticket(requests.Capture(), ClientProtocolJson.Copy(configuration)));
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
        var result = new Generation(configuration, substrate);
        configuration = null; substrate = null;
        // Retire pending download tickets as ownership moves into generation.
        bool keepInitial = initial, keepLanguage = languageAccepted;
        requests.Reset(); initial = keepInitial; languageAccepted = keepLanguage;
        return result;
    });
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
    internal sealed record Generation(ModWorldConfigurationDto Configuration, WorldSubstratePackage Substrate);
}
