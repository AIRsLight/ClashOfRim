using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientWorldPublicationTests
{
    private static int scenarios;
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture; scenarios = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (var mode in Enum.GetValues<ClientWorldProjection>())
                {
                    foreach (bool adoptWorld in new[] { false, true }) Basic(mode, adoptWorld);
                    foreach (string edit in new[] { "new", "reset", "same", "failed-persist" }) LocalEdit(mode, edit);
                    foreach (var other in Enum.GetValues<ClientWorldProjection>()) Reverse(mode, other);
                    foreach (string change in new[] { "user", "auth", "server", "colony", "login", "generation", "world-id", "game", "world", "unavailable", "markers-reset", "snapshot" })
                        Ownership(mode, change);
                    foreach (string phase in new[] { "remember", "extensions", "write", "proxy", "summary" }) Reentry(mode, phase);
                    MarkerRace(mode); Failure(mode); Empty(mode); AdoptionOwnership(mode);
                }
                PreferenceReentry(); QueueAfterSourceCompletion(); OwnedConfiguration();
            }
            Console.WriteLine($"  world publication: {scenarios} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static void Basic(ClientWorldProjection mode, bool adoptWorld)
    {
        var f = new Fixture(); var observation = f.Flow.Capture();
        var data = Data("server", adoptWorld ? "next-world" : "world");
        Check(f.Publish(observation, data, mode), "Accepted view failed.");
        Check(f.Id == data.WorldConfigurationId && f.P.Appearance.Read().ColorHex == "server" && f.Label == "server", "World adoption lost a projection.");
        Check(f.P.Sites.IsBlocked(4, 7, out var site) && site!.Label == "server", "Site cache was not replaced.");
        int effects = f.P.Trace.Count + f.Projects;
        f.Publish(observation, Data("duplicate", f.Id), mode);
        Check(effects == f.P.Trace.Count + f.Projects, "Receipt was applied twice.");
        data.PlayerColonySites[0].Appearance!.ColorHex = "mutated";
        Check(f.P.Appearance.Read().ColorHex == "server", "Source DTO leaked into preference."); scenarios++;
    }
    private static void LocalEdit(ClientWorldProjection mode, string edit)
    {
        var f = new Fixture(); f.P.Appearance.SaveCurrent(Selection("old"));
        var receipt = f.Flow.Capture();
        if (edit == "failed-persist") f.P.Hook = phase => { if (phase == "persist") throw new InvalidOperationException("persist"); };
        string value = edit == "reset" ? "" : edit == "same" ? "old" : "local";
        try { f.P.Appearance.SaveCurrent(Selection(value)); }
        catch (InvalidOperationException ex) when (ex.Message == "persist") { }
        f.P.Hook = null;
        Check(f.Publish(receipt, Data("stale"), mode), "Local edit blocked unrelated accepted configuration.");
        Check(f.P.Appearance.Read().ColorHex == value, "Old server echo overwrote local intent: " + edit);
        f.Publish(f.Flow.Capture(), Data("fresh"), mode);
        Check(f.P.Appearance.Read().ColorHex == "fresh", "Later server observations were permanently disabled."); scenarios++;
    }
    private static void Reverse(ClientWorldProjection oldMode, ClientWorldProjection newMode)
    {
        var f = new Fixture(); var old = f.Flow.Capture(); var newer = f.Flow.Capture();
        Check(f.Publish(newer, Data("new"), newMode) && f.Publish(old, Data("old"), oldMode), "Superseded accepted work could not complete.");
        Check(f.Label == "new" && f.P.Appearance.Read().ColorHex == "new" && f.LastConfiguration == (newMode == ClientWorldProjection.Sites ? "" : "new"), "Cross-source late receipt replaced newer view."); scenarios++;
    }
    private static void Ownership(ClientWorldProjection mode, string change)
    {
        var f = new Fixture(); var receipt = f.Flow.Capture();
        switch (change)
        {
            case "user": f.Ports.User = "other"; break; case "auth": f.Ports.Auth = "other"; break;
            case "server": f.Ports.Server = "http://other"; break; case "colony": f.Ports.Colony = "other"; break;
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = f.Ports.User, AuthToken = f.Ports.Auth, SessionId = "new" }); break;
            case "generation": ClientSessionWork.Reset(); break; case "world-id": f.Id = "other"; break;
            case "game": f.Ports.GameOwner = new(); break; case "world": f.World = new(); break;
            case "unavailable": f.Session.BeginUnavailable(f.Ports.Auth, null); break;
            case "markers-reset": f.P.Markers.Reset(); break; case "snapshot": f.Ports.Snapshot = "advanced"; break;
        }
        bool applied = f.Publish(receipt, Data("old"), mode);
        Check(applied == (change == "snapshot") && (f.P.Trace.Count > 0) == applied, "Observation crossed ownership: " + change); scenarios++;
    }
    private static void Reentry(ClientWorldProjection mode, string phase)
    {
        if (mode == ClientWorldProjection.Sites && phase == "extensions") return;
        var f = new Fixture(); bool fired = false;
        void Hook(string at)
        {
            if (at != phase || fired) return; fired = true;
            Check(f.Publish(f.Flow.Capture(), Data("new", f.Id), mode), "Reentrant newer view rejected.");
        }
        f.Hook = Hook; f.P.Hook = Hook;
        f.Publish(f.Flow.Capture(), Data("old"), mode);
        Check(fired && f.Label == "new" && f.P.Appearance.Read().ColorHex == "new", "Reentrant publication was overwritten at " + phase); scenarios++;
    }
    private static void MarkerRace(ClientWorldProjection mode)
    {
        var f = new Fixture(); var old = f.Flow.Capture(); var recent = f.P.Markers.CaptureObservation();
        f.P.Markers.Observe(recent, Markers("new"), immediately: true);
        f.Publish(old, Data("old"), mode);
        Check(f.Label == "new" && f.P.Sites.IsBlocked(4, 7, out var site) && site!.Label == "new", "Older catalog replaced a newer marker/site delta."); scenarios++;
    }
    private static void AdoptionOwnership(ClientWorldProjection mode)
    {
        foreach (bool reset in new[] { false, true })
        {
            var f = new Fixture(); var receipt = f.Flow.Capture();
            f.Hook = phase =>
            {
                if (phase != "remember") return; f.Hook = null;
                if (reset) f.P.Markers.Reset();
                else f.P.Appearance.SaveCurrent(Selection("new-world-local"));
            };
            bool applied = f.Publish(receipt, Data("old", "next-world"), mode);
            Check(applied != reset && (reset ? f.P.Trace.Count == 0 : f.P.Appearance.Read().ColorHex == "new-world-local"),
                "World adoption ignored an explicit reset or a local edit in the new account key."); scenarios++;
        }
    }
    private static void Failure(ClientWorldProjection mode)
    {
        var f = new Fixture(); f.P.Hook = phase => { if (phase == "summary") throw new InvalidOperationException("injected"); };
        try { f.Publish(f.Flow.Capture(), Data("old"), mode); throw new Exception("Expected fault."); }
        catch (InvalidOperationException ex) when (ex.Message == "injected") { }
        f.P.Hook = null;
        Check(f.Publish(f.Flow.Capture(), Data("retry"), mode) && f.Label == "retry", "Projection exception blocked the next receipt."); scenarios++;
    }
    private static void Empty(ClientWorldProjection mode)
    {
        var f = new Fixture(); f.Publish(f.Flow.Capture(), Data("old"), mode);
        Check(f.Flow.Publish(f.Flow.Capture(), null, mode, () => true, ("empty", null)), "Null view failed.");
        Check(f.P.Markers.Snapshot.Count == 0 && !f.P.Sites.IsBlocked(4, 7, out _) && f.P.Appearance.Read().ColorHex == "old", "Empty authoritative view or empty-appearance policy changed."); scenarios++;
    }
    private static void PreferenceReentry()
    {
        var f = new Fixture(); bool changed = false;
        f.P.Hook = phase => { if (phase == "write" && !changed) { changed = true; f.P.Appearance.SaveCurrent(Selection("local")); } };
        f.Publish(f.Flow.Capture(), Data("old"), ClientWorldProjection.Catalog);
        Check(f.P.Appearance.Read().ColorHex == "local", "Server persistence overwrote a reentrant local edit."); scenarios++;
    }
    private static void QueueAfterSourceCompletion()
    {
        foreach (bool changeOwner in new[] { false, true })
        {
            var f = new Fixture(); bool requestCurrent = true;
            f.Flow.Publish(f.Flow.Capture(), Data("accepted"), ClientWorldProjection.Catalog, () => requestCurrent, ("queued", Markers("accepted")));
            requestCurrent = false;
            if (changeOwner) f.Ports.GameOwner = new();
            f.Ports.PumpUntil(Task.CompletedTask).GetAwaiter().GetResult();
            Check(f.P.Trace.Contains("markers") != changeOwner, "Queued projection lost cache ownership or followed an obsolete game."); scenarios++;
        }
    }
    private static void OwnedConfiguration()
    {
        foreach (bool owned in new[] { false, true })
        {
            var f = new Fixture(); var source = Data("server");
            f.Applying = data =>
            {
                Check(ReferenceEquals(source, data) == owned, "Owned response was recopied or borrowed input was not isolated.");
                data.PlayerColonySites[0].Label = "callback mutation";
                data.PlayerColonySites[0].Appearance!.ColorHex = "callback mutation";
            };
            var observation = f.Flow.Capture();
            bool ok = owned ? f.Flow.PublishOwned(observation, source, ClientWorldProjection.Catalog, () => true)
                : f.Flow.Publish(observation, source, ClientWorldProjection.Catalog, () => true);
            Check(ok && f.P.Sites.IsBlocked(4, 7, out var site) && site!.Label == "server" && f.P.Appearance.Read().ColorHex == "server",
                "Configuration callback mutated the independently captured site view.");
            if (!owned) Check(source.PlayerColonySites[0].Label == "server", "Borrowed source was mutated by configuration projection.");
            scenarios++;
        }
    }
    internal static ColonyAppearanceSelection Selection(string value) => new(null, null, null, value);
    internal static ModWorldConfigurationDto Data(string value, string world = "world") => new()
    {
        WorldConfigurationId = world, SeedString = value,
        PlayerColonySites = new() { new() { Tile = 4, TileLayerId = 7, UserId = "alice", ColonyId = "colony", Label = value, Appearance = new() { ColorHex = value } },
            new() { Tile = 8, TileLayerId = 0, UserId = "bob", ColonyId = "remote" } }
    };
    internal static ModWorldMapMarkerDeliveryDto Markers(string value) => new()
    { Markers = new() { new() { Kind = "TradeableColony", Tile = 4, TileLayerId = 7, OwnerUserId = "alice", OwnerColonyId = "colony", Label = value } } };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IClientWorldConfigurationRuntime, IClientWorldConfigurationStore, IClientStorytellerRuntime
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldPublicationTestPorts P;
        internal ClientWorldPublication Flow => P.Flow;
        internal string Id = "world", LastConfiguration = "";
        internal object World = new(); internal int Projects; internal Action<string>? Hook;
        internal Action<ModWorldConfigurationDto>? Applying;
        internal string? Label => P.Markers.Snapshot.SingleOrDefault()?.Label;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports));
            var storyteller = new ClientStorytellerConfiguration(Session, new(Session), this);
            P = new(Session, Ports, () => Id, () => World, new(Session, this, this, storyteller, null!));
        }
        internal bool Publish(ClientWorldPublication.Observation observation, ModWorldConfigurationDto data, ClientWorldProjection mode)
        {
            bool published = Flow.Publish(observation, data, mode, () => true, ("test", Markers(data.SeedString)));
            Ports.PumpUntil(Task.CompletedTask).GetAwaiter().GetResult();
            return published;
        }
        public void Remember(string id) { Id = id; Hook?.Invoke("remember"); }
        public IClientWorldConfigurationSession Bind() => new Bound(this, Ports.GameOwner, World);
        IClientStorytellerSession IClientStorytellerRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        private sealed record Bound(Fixture F, object Game, object World) : IClientWorldConfigurationSession, IClientStorytellerSession
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public bool IsPlaying => true;
            public ClientWorldFeatureNames? ApplyFeatures(ModWorldConfigurationDto config) => null;
            public void ApplyFactions(ModWorldConfigurationDto config) { }
            public void ApplyRoads(ModWorldConfigurationDto config) { }
            public void ApplyObjectLabels(ModWorldConfigurationDto config) { }
            public void ApplyExtensions(ModWorldConfigurationDto config, string user, bool state) { F.Projects++; F.LastConfiguration = config.SeedString; F.Applying?.Invoke(config); F.Hook?.Invoke("extensions"); }
            public ClientStorytellerBaseline Capture() => new(null, null, null);
            public IClientStorytellerChange? Prepare(ClientStorytellerBaseline value) => null;
            public void LockCommitment() { }
        }
    }
}
