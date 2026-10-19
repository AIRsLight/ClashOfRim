using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientWorldSetupPage { bool IsOpen { get; } }

// The starting-site visit owns its polling deadline. Initial provisioning and
// marker work remain in their existing modules, bound to the visit's source.
internal sealed class ClientWorldSetupLifecycle
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientRequestTracker<IClientWorldCatalogSession> scopes;
    private readonly ClientWorldSourceGame sourceGame;
    private readonly ClientInitialWorldSetup initial;
    private readonly ClientColonyAppearance appearance;
    private readonly ClientWorldSynchronization synchronization;
    private readonly ClientWorldMarkers markers;
    private readonly IClientWorldPreparationPresentation view;
    internal ClientWorldSetupLifecycle(ClientSessionLifecycle session, IClientWorldCatalogRuntime runtime, ClientWorldSourceGame sourceGame,
        ClientInitialWorldSetup initial, ClientColonyAppearance appearance, ClientWorldSynchronization synchronization,
        ClientWorldMarkers markers, IClientWorldPreparationPresentation view)
    {
        this.session = session; this.sourceGame = sourceGame; this.initial = initial; this.appearance = appearance;
        this.synchronization = synchronization; this.markers = markers; this.view = view;
        scopes = new(session, runtime.Bind, bound => bound.IsCurrent, static () => { });
    }
    internal Visit Open(IClientWorldSetupPage page)
    {
        using var p = ClientSessionWork.EnterPublication();
        return new(this, scopes.Capture(), page);
    }
    internal Task InitializedAsync(object game)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = scopes.Capture();
        return sourceGame.Initialized(game) ? Submit(() => Owns(scope)) : Task.CompletedTask;
    }
    private bool Owns(ClientRequestTracker<IClientWorldCatalogSession>.Scope scope)
        => !session.IsUnavailable && scopes.TryApply(scope, static () => { });
    private Task Submit(Func<bool> current)
    {
        if (!current()) return Task.CompletedTask;
        var selected = appearance.Capture();
        return current() ? initial.StartAsync(selected) : Task.CompletedTask;
    }
    internal sealed class Visit
    {
        private readonly ClientWorldSetupLifecycle owner;
        private readonly ClientRequestTracker<IClientWorldCatalogSession>.Scope scope;
        private readonly IClientWorldSetupPage page;
        private bool started, closed;
        private readonly ClientWorldMarkerRefreshSchedule refresh = new();
        internal Visit(ClientWorldSetupLifecycle owner, ClientRequestTracker<IClientWorldCatalogSession>.Scope scope, IClientWorldSetupPage page)
        { this.owner = owner; this.scope = scope; this.page = page; }
        private bool Current() => !closed && page.IsOpen && owner.Owns(scope);
        internal Task StartAsync(float now)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (started || !Current()) return Task.CompletedTask;
            started = true; refresh.Advance(true, now);
            var submission = owner.Submit(Current);
            return Task.WhenAll(submission, Refresh("ClashOfRim.WorldMap.ReasonStartingSiteEntered"));
        }
        internal Task UpdateAsync(float now)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (!started || !Current()) return Task.CompletedTask;
            owner.markers.ApplyCached();
            if (!Current() || refresh.Advance(true, now) is null) return Task.CompletedTask;
            return Refresh("ClashOfRim.WorldMap.ReasonStartingSiteStayed");
        }
        private Task Refresh(string key)
        {
            if (!Current()) return Task.CompletedTask;
            string reason = owner.view.Text(key);
            return Current() ? owner.synchronization.RequestAsync(ClientWorldSynchronizationKind.Markers, reason) : Task.CompletedTask;
        }
        internal void Close() { using var p = ClientSessionWork.EnterPublication(); closed = true; }
    }
}
