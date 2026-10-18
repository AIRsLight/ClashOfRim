using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;

// Real publication/cache/preference modules with narrow in-memory engine/store
// ports. Workflow tests reuse this instead of replacing publication with a callback.
internal sealed class ClientWorldPublicationTestPorts : IClientWorldIdentity, IClientWorldMarkersRuntime,
    IClientWorldMarkersPresentation, IClientOccupiedColonySitesRuntime, IClientColonyAppearanceStore, IClientColonyAppearancePresentation
{
    private readonly EventModuleTestPorts ports;
    private readonly Func<string> worldId;
    private readonly Func<object> world;
    internal readonly ClientWorldPublication Flow;
    internal readonly ClientWorldMarkers Markers;
    internal readonly ClientColonyAppearance Appearance;
    internal readonly ClientOccupiedColonySites Sites;
    internal readonly Dictionary<string, string> Preferences = new();
    internal readonly List<string> Trace = new();
    internal Action<string>? Hook;
    internal ClientWorldPublicationTestPorts(ClientSessionLifecycle session, EventModuleTestPorts ports,
        Func<string> worldId, Func<object> world, ClientWorldConfiguration configuration, ClientOccupiedColonySites? sites = null)
    {
        this.ports = ports; this.worldId = worldId; this.world = world;
        Sites = sites ?? new(session, this, this, configuration);
        Markers = new(session, this, Sites, this, this);
        Appearance = new(session, this, this);
        Flow = new(this, configuration, Sites, Appearance, Markers);
    }
    public string WorldId => worldId();
    public IClientWorldMarkersSession Bind() => new Bound(this, ports.GameOwner, world());
    IClientOccupiedColonySitesSession IClientOccupiedColonySitesRuntime.Bind() => new Bound(this, ports.GameOwner, world());
    public ClientColonyAppearanceAccount ReadAccount() => new(ports.Server, WorldId, ports.User, ports.Colony);
    public string? Read(string key) => Preferences.GetValueOrDefault(key);
    public void Write(string key, string? value) { if (value is null) Preferences.Remove(key); else Preferences[key] = value; Step("write"); }
    public void Persist() => Step("persist");
    public void Open(string colony, ColonyAppearanceSelection selected, Action<ColonyAppearanceSelection> save) => throw new NotSupportedException();
    public string Text(string key, params (string Name, object? Value)[] args) => key;
    public void Message(string message, ClientWorldWriteMessage kind) => Step("message");
    public string Summary(IReadOnlyCollection<ModWorldMapMarkerDto> markers) { Step("summary"); return "markers"; }
    public void Status(string message) => Step("status");
    public void Diagnose(string message) => Step("diagnostic");
    public void Diagnose(string message, bool warning) => Step("diagnostic");
    private void Step(string point) { Trace.Add(point); Hook?.Invoke(point); }
    internal sealed class DelayedReply
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Wait() { entered.TrySetResult(); return release.Task; }
        internal async Task Complete(EventModuleTestPorts ports, Task work, Action during)
        {
            try { await ports.PumpUntil(entered.Task); during(); }
            finally { release.TrySetResult(); }
            await ports.PumpUntil(work);
        }
    }
    private sealed record Bound(ClientWorldPublicationTestPorts F, object Game, object World) : IClientWorldMarkersSession, IClientOccupiedColonySitesSession
    {
        public bool IsCurrent => ReferenceEquals(Game, F.ports.GameOwner) && ReferenceEquals(World, F.world());
        public bool HasWorldObjects => true;
        public void Enqueue(Action action, Action discard) => F.ports.Enqueue(action);
        public ClientWorldMarkersApplied Apply(IReadOnlyCollection<ModWorldMapMarkerDto> markers, string user) { F.Step("markers"); return new(true, "applied"); }
        public bool CanReserve(int tile, int layer) => tile >= 0;
        public bool IsNeighborOrSame(int tile, int other, int layer) => tile == other;
        public void EnsureProxy(string user) => F.Step("proxy");
    }
}
