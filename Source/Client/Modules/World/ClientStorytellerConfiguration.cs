using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.World;

internal sealed record ClientStorytellerBaseline(string? Storyteller, string? Difficulty, string? ValuesXml, string? CaptureFailure = null)
{
    internal bool HasDefinitions => !string.IsNullOrWhiteSpace(Storyteller) && !string.IsNullOrWhiteSpace(Difficulty);
    internal string Signature => string.Join("\u001f", Storyteller ?? "", Difficulty ?? "", ValuesXml ?? "");
    internal static ClientStorytellerBaseline From(ModWorldConfigurationDto configuration)
        => new(configuration.StorytellerDefName, configuration.DifficultyDefName, configuration.DifficultyValuesXml);
}
internal interface IClientStorytellerRuntime { IClientStorytellerSession Bind(); }
internal interface IClientStorytellerSession
{
    bool IsCurrent { get; }
    bool IsPlaying { get; }
    ClientStorytellerBaseline Capture();
    IClientStorytellerChange? Prepare(ClientStorytellerBaseline baseline);
    void LockCommitment();
}
internal interface IClientStorytellerChange { void Apply(); }
internal interface IClientStorytellerPage
{
    bool IsCurrent { get; }
    void Continue();
}

// Both world setup and runtime refresh use the same baseline and publication path.
// Scribe/Def lookup may call mod code: stage first, then revalidate ownership before
// changing the active storyteller or advancing a setup page.
internal sealed class ClientStorytellerConfiguration
{
    private readonly ClientWorldSetupState setup;
    private readonly ClientRequestTracker<IClientStorytellerSession> requests;
    internal ClientStorytellerConfiguration(ClientSessionLifecycle session, ClientWorldSetupState setup, IClientStorytellerRuntime runtime)
    { this.setup = setup; requests = new(session, runtime.Bind, bound => bound.IsCurrent, static () => { }); }
    internal ClientStorytellerBaseline Capture()
    {
        var scope = requests.Capture();
        var value = scope.Boundary.Capture();
        return requests.TryApply(scope, static () => { }) ? value : new(null, null, null);
    }
    internal bool ApplyRuntime(ModWorldConfigurationDto configuration, Func<bool>? current = null)
    {
        var scope = requests.Capture();
        return scope.Boundary.IsPlaying && Apply(scope, ClientStorytellerBaseline.From(configuration), current);
    }
    internal bool ContinueSetup(IClientStorytellerPage page)
    {
        var scope = requests.Capture();
        var ticket = setup.CaptureConfiguration();
        if (ticket is null || !page.IsCurrent) return false;
        bool CurrentPage() => setup.IsCurrent(ticket) && page.IsCurrent;
        if (!Apply(scope, ClientStorytellerBaseline.From(ticket.Configuration), CurrentPage)) return false;
        bool continued = false;
        requests.TryApply(scope, () => { if (CurrentPage()) { page.Continue(); continued = true; } });
        return continued;
    }
    internal void LockCommitment()
    {
        if (!setup.Pending) return;
        var scope = requests.Capture();
        requests.TryApply(scope, scope.Boundary.LockCommitment);
    }
    private bool Apply(ClientRequestTracker<IClientStorytellerSession>.Scope scope, ClientStorytellerBaseline baseline, Func<bool>? current = null)
    {
        if (!baseline.HasDefinitions || !requests.TryApply(scope, static () => { }) || current?.Invoke() == false) return false;
        var change = scope.Boundary.Prepare(baseline);
        if (change is null) return false;
        bool applied = false;
        requests.TryApply(scope, () =>
        {
            if (current?.Invoke() == false) return;
            change.Apply(); applied = true;
        });
        return applied;
    }
}
