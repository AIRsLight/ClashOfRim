using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class IdeologyCatalogBindingTests
{
    private static IdeoManager ideos = null!;
    private static FactionManager factions = null!;
    private static World world = null!;
    private static int normalizations;
    private static bool Normalize() { normalizations++; return false; }
    private static bool ReadIdeos(ref IdeoManager __result) { __result = ideos; return false; }
    private static bool ReadFactions(ref FactionManager __result) { __result = factions; return false; }
    private static bool ReadWorld(ref World __result) { __result = world; return false; }

    public static void Run()
    {
        ServerFactionIdeologyBindingTests.Run();
        var previous = ClientSharedIdeologyWorld.Current;
        var previousGame = Current.Game;
        ClientSharedIdeologyWorld.Attach(null); Current.Game = null; world = null!;
        var harmony = new Harmony("ClashOfRim.Tests.IdeologyBinding");
        ideos = new IdeoManager();
        factions = new FactionManager();
        var stale = new Ideo { id = 1, name = "Stale", initialPlayerIdeo = true };
        var live = new Ideo { id = 2, name = "Live" };
        ideos.IdeosListForReading.Add(stale);
        ideos.IdeosListForReading.Add(live);
        var faction = new Faction { def = new FactionDef { defName = "OutlanderCivil" } };
        faction.ideos = new FactionIdeosTracker(faction);
        faction.ideos.SetPrimary(live);
        factions.AllFactionsListForReading.Add(faction);
        var dto = new ModWorldIdeoSummaryDto { GlobalKey = "owner:server/ideo:7", OwnerUserId = "server", FactionDefName = "OutlanderCivil", Name = "Live" };
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.IdeoManager)), prefix: new HarmonyMethod(typeof(IdeologyCatalogBindingTests), nameof(ReadIdeos)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.FactionManager)), prefix: new HarmonyMethod(typeof(IdeologyCatalogBindingTests), nameof(ReadFactions)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.World)), prefix: new HarmonyMethod(typeof(IdeologyCatalogBindingTests), nameof(ReadWorld)));
            harmony.Patch(AccessTools.Method(typeof(RemoteIdeoMaterializer), "NormalizeGeneratedIdeologies"), prefix: new HarmonyMethod(typeof(IdeologyCatalogBindingTests), nameof(Normalize)));
            RemoteIdeoCatalog.Register(stale, dto.GlobalKey);
            if (!RemoteIdeoMaterializer.TryGetOrCreate(dto, out var resolved) || !ReferenceEquals(resolved, live))
                throw new Exception("A superseded catalog entry won over the faction's current primary ideology.");
            if (!RemoteIdeoCatalog.TryGetCatalogIdeo(dto.GlobalKey, out resolved) || !ReferenceEquals(resolved, live))
                throw new Exception("Catalog was not rebound to the live faction instance.");

            var player = new Faction { def = new FactionDef { defName = "PlayerColony", isPlayer = true } };
            factions.AllFactionsListForReading.Add(player);
            ideos.classicMode = true;
            if (!ClassicIdeoSetupPatch.ClassicFactions(factions).SequenceEqual(new[] { player }))
                throw new Exception("Classic setup included authoritative NPC factions before map login.");
            if (ideos.classicMode)
                throw new Exception("The player's Classic choice disabled full ideologies for the shared world.");

            dto.GlobalKey = "owner:alice/ideo:7"; dto.OwnerUserId = "alice";
            RemoteIdeoCatalog.Register(stale, dto.GlobalKey);
            if (!RemoteIdeoMaterializer.TryGetOrCreate(dto, out resolved) || !ReferenceEquals(resolved, stale))
                throw new Exception("A player-owned ideology was replaced by a native faction ideology.");

            RemoteIdeoCatalog.Unregister(live);
            RemoteIdeoCatalog.Unregister(stale);
            ideos.classicMode = true;
            if (!ClassicIdeoSetupPatch.ClassicFactions(factions).SequenceEqual(factions.AllFactions))
                throw new Exception("Single-player Classic setup no longer includes every faction.");
            if (!ideos.classicMode)
                throw new Exception("Single-player Classic mode was disabled.");

            SharedWorldLifecycle(player);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            RemoteIdeoCatalog.Unregister(stale); RemoteIdeoCatalog.Unregister(live);
            ClientSharedIdeologyWorld.Attach(previous); Current.Game = previousGame; ClientSessionWork.Reset();
        }
    }

    private static void SharedWorldLifecycle(Faction player)
    {
        var opened = AccessTools.Method(typeof(SharedIdeologySetupWorldPatch), "Prefix").CreateDelegate<Action>();
        foreach (bool initial in new[] { false, true })
        {
            var f = Setup(initial); var original = world;
            Check(ClientSharedIdeologyWorld.IsSharedWorld && normalizations == 0, "Prepared world lost shared ideology eligibility.");
            opened(); f.Setup.Reset();
            Check(normalizations == 1 && ClientSharedIdeologyWorld.IsSharedWorld, "Host setup lost its World when publication consumed Pending.");
            ideos.classicMode = true;
            Check(ClassicIdeoSetupPatch.ClassicFactions(factions).SequenceEqual(new[] { player }) && !ideos.classicMode,
                "Classic selection changed authoritative NPC ideologies.");
            PlayerIdeoForkPatches.Postfix();
            Check(normalizations == 2, "Player ideology completion bypassed the shared qualifier.");
            world = New<World>(); ideos.classicMode = true; opened(); PlayerIdeoForkPatches.Postfix();
            Check(!ClientSharedIdeologyWorld.IsSharedWorld && normalizations == 2
                && ClassicIdeoSetupPatch.ClassicFactions(factions).SequenceEqual(factions.AllFactions) && ideos.classicMode,
                "Shared ideology setup leaked into a different local World.");
            world = original;
            Check(ClientSharedIdeologyWorld.IsSharedWorld, "Remembered eligibility stopped following the actual World.");
            ClientSharedIdeologyWorld.Attach(new(f.Setup, f.Session));
            Check(!ClientSharedIdeologyWorld.IsSharedWorld, "New adapter inherited another adapter's remembered World.");
        }
        foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "reset", "snapshot", "password", "steam", "game", "unavailable" })
        {
            var f = Setup(initial: true);
            switch (change)
            {
                case "user": f.Ports.User = "other"; break;
                case "server": f.Ports.Server = "http://other"; break;
                case "colony": f.Ports.Colony = "other"; break;
                case "auth": f.Ports.Auth = "other"; break;
                case "login": f.Session.PublishLogin(new() { AuthToken = f.Ports.Auth }); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "reset": f.Setup.Reset(); break;
                case "snapshot": f.Ports.Snapshot = "next"; break;
                case "password": f.Ports.Password = "next"; break;
                case "steam": f.Ports.SteamTicket = "next"; break;
                case "game": f.Ports.GameOwner = new(); Current.Game = New<Game>(); break;
                case "unavailable": f.Session.BeginUnavailable(f.Ports.Auth, null); break;
            }
            bool eligible = change is "snapshot" or "password" or "steam" or "game" or "unavailable";
            Check(ClientSharedIdeologyWorld.IsSharedWorld == eligible, "Setup eligibility bypassed original identity rules: " + change);
            opened();
            Check(normalizations == (eligible ? 1 : 0), "Starting-site normalization crossed setup ownership: " + change);
        }
        foreach (string active in new[] { "local", "snapshot", "observation", "battle", "unbound", "missing-world" })
        {
            var f = Setup(initial: true); f.Setup.Reset();
            var game = New<Game>(); Current.Game = game;
            AccessTools.Field(typeof(Game), "components").SetValue(game, new List<GameComponent> { new ClashOfRimGameComponent(game) });
            if (active == "snapshot") f.Session.PublishLogin(new() { AuthToken = "auth", SessionId = "session" });
            if (active is "observation" or "unbound") ClashOfRimGameComponent.SetActiveObservationSession(ActiveRemoteMapSession.FromObservation("observe", "Scout", "bob", "c", "s"));
            if (active == "battle") ClashOfRimGameComponent.SetActiveRaidBattleSession(new() { EventId = "raid" });
            if (active == "unbound") ClientSharedIdeologyWorld.Attach(null);
            if (active == "missing-world") { Prepare(f.Setup, true); world = null!; }
            Check(ClientSharedIdeologyWorld.IsSharedWorld == (active is "snapshot" or "observation" or "battle" or "missing-world"), "Shared multiplayer read diverged: " + active);
            opened();
            Check(normalizations == 0, "Non-setup session or absent World triggered setup normalization: " + active);
        }
    }
    private static (EventModuleTestPorts Ports, ClientSessionLifecycle Session, ClientWorldSetupState Setup) Setup(bool initial)
    {
        ClientSessionWork.Reset(); Current.Game = null; world = New<World>(); normalizations = 0;
        var ports = new EventModuleTestPorts(); var session = new ClientSessionLifecycle(ports, ports, new(ports));
        var setup = new ClientWorldSetupState(session); Prepare(setup, initial);
        ClientSharedIdeologyWorld.Attach(new(setup, session)); return (ports, session, setup);
    }
    private static void Prepare(ClientWorldSetupState setup, bool initial) => setup.Prepare(new()
    {
        Result = new() { Accepted = true }, RequiresInitialWorldConfiguration = initial,
        WorldConfigured = !initial, WorldConfiguration = initial ? null : new()
    });
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
