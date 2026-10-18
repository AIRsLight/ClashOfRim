using System;
using System.Globalization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.World;

internal sealed record ClientWorldGenerationInput(string Seed, float Coverage, ModWorldConfigurationDto Configuration)
{
    internal static bool TryCoverage(string? text, out float coverage)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out coverage) && !float.IsNaN(coverage) && !float.IsInfinity(coverage) && coverage > 0f && coverage <= 1f;
    internal static ClientWorldGenerationInput Read(ModWorldConfigurationDto configuration)
    {
        if (!TryCoverage(configuration.PlanetCoverage, out float coverage)) throw new ArgumentException("Invalid server planet coverage: " + configuration.PlanetCoverage);
        return new(string.IsNullOrWhiteSpace(configuration.SeedString) ? "ClashOfRim" : configuration.SeedString!, coverage, configuration);
    }
}
internal interface IClientWorldGenerationRuntime { IClientWorldGenerationSession Bind(); }
internal interface IClientWorldGenerationSession
{
    bool IsCurrent { get; }
    void Queue(Action generate);
    void WhenFinished(Action complete);
    IClientGeneratedWorld Generate(ClientWorldGenerationInput input, Func<bool> current);
}
internal interface IClientGeneratedWorld : IDisposable
{
    void Publish();
    void ApplySubstrate(WorldSubstratePackage substrate, ModWorldConfigurationDto configuration);
    void Complete(Func<bool> current);
}
internal interface IClientWorldGenerationPage
{
    bool IsCurrent { get; }
    void Close();
    void Reopen();
    void Continue(Func<bool> current);
}
internal interface IClientWorldGenerationPresentation { void Generated(string seed); void Failed(Exception exception); }

// Owns consumption, generation, configuration projection and the exact completion.
// Expensive generation runs outside the session publication lock. Each subsequent
// publication binds both the captured Game and the consumed setup plan.
internal sealed class ClientWorldGeneration
{
    private readonly ClientWorldSetupState setup;
    private readonly ClientWorldConfiguration configuration;
    private readonly IClientWorldGenerationPresentation presentation;
    private readonly ClientRequestTracker<IClientWorldGenerationSession> requests;
    internal ClientWorldGeneration(ClientSessionLifecycle session, ClientWorldSetupState setup, ClientWorldConfiguration configuration,
        IClientWorldGenerationRuntime runtime, IClientWorldGenerationPresentation presentation)
    { this.setup = setup; this.configuration = configuration; this.presentation = presentation; requests = new(session, runtime.Bind, bound => bound.IsCurrent, static () => { }); }
    internal bool TryStart(IClientWorldGenerationPage page)
    {
        if (requests.Busy) return true;
        if (!setup.HasConfiguration) return false;
        if (!page.IsCurrent) return true;
        var request = requests.Begin()!;
        var plan = setup.TakeGeneration();
        if (plan is null) { requests.End(request); return setup.HasConfiguration; }
        var runtime = request.Scope.Boundary;
        IClientGeneratedWorld? generated = null;
        int phase = 0;
        bool Current() => setup.IsCurrent(plan) && requests.TryApply(request, static () => { });
        bool Step(Action action) => requests.TryApply(request, () => { if (setup.IsCurrent(plan)) action(); }) && Current();
        void Discard() { generated?.Dispose(); requests.End(request); }
        void Fail(Exception exception)
        {
            try
            {
                if (!Current()) return;
                generated?.Dispose(); generated = null;
                if (!setup.RestoreGeneration(plan)) return;
                Step(() => presentation.Failed(exception));
                Step(page.Reopen);
            }
            finally { Discard(); }
        }
        try
        {
            var input = ClientWorldGenerationInput.Read(plan.Configuration);
            if (!Step(page.Close)) { Discard(); return true; }
            runtime.Queue(() =>
            {
                bool start = false;
                Step(() => { if (phase == 0) { phase = 1; start = true; } });
                if (!start) { if (!Current()) Discard(); return; }
                try
                {
                    generated = runtime.Generate(input, Current);
                    if (!Step(generated.Publish)) { Discard(); return; }
                    if (!Step(() => generated.ApplySubstrate(plan.Substrate, plan.Configuration))) { Discard(); return; }
                    bool applied = false;
                    if (!Step(() => applied = configuration.Apply(plan.Configuration, worldState: true, features: false))) { Discard(); return; }
                    if (!applied) throw new InvalidOperationException("World configuration ownership changed during application.");
                    runtime.WhenFinished(() =>
                    {
                        bool finish = false;
                        Step(() => { if (phase == 1) { phase = 2; finish = true; } });
                        if (!finish) { if (!Current()) Discard(); return; }
                        bool continuePage = false;
                        try
                        {
                            if (!Step(() => generated.Complete(Current))) return;
                            if (!Step(() => presentation.Generated(input.Seed))) return;
                            // Retire before invoking the next page/action, which can start a new Game.
                            requests.End(request);
                            continuePage = true;
                        }
                        catch (Exception ex) { Fail(ex); }
                        finally { Discard(); }
                        bool OwnsContinuation() => setup.IsCurrent(plan) && requests.TryApply(request.Scope, static () => { });
                        if (continuePage && OwnsContinuation()) page.Continue(OwnsContinuation);
                    });
                }
                catch (Exception ex) { runtime.WhenFinished(() => Fail(ex)); }
            });
        }
        catch (Exception ex) { Fail(ex); }
        return true;
    }
}
