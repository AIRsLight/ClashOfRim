using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

// Actual force/source/battlefield adapters, with only engine/service effects patched.
// This verifies ownership and dispatch; engine spawning and map sanitation need RimTest.
internal static class ClientRaidAttackForceTests
{
    private static Game game = null!;
    private static World world = null!;
    private static Map map = null!;
    private static Faction player = null!;
    private static Faction? defender;
    private static readonly HashSet<Thing> destroyed = new();
    private static readonly HashSet<Pawn> dead = new(), nonOwners = new(), prisoners = new();
    private static readonly List<string> trace = new();
    private static readonly List<Pawn> members = new();
    private static readonly Dictionary<ActiveTransporterInfo, Thing> shuttles = new();
    private static readonly List<Thing> referenced = new();
    private static readonly List<ActiveTransporterInfo> landed = new();
    private static ActiveRaidBattleSession? registered;
    private static Caravan caravan = null!;
    private static bool caravanDestroyed, caravanInWorld, landingValid;
    private static int tile, layer;
    private static readonly IntVec3 edge = new(1, 0, 8), landing = new(4, 0, 9);
    private static readonly ClashOfRimClientNetworkContext context = new("http://server", "user-é", "colony", "base", null, null, "auth");

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.RaidAttackForce");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            Install(harmony);
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                CaptureAndRevalidate();
                Battlefield();
            }
            CaravanSource();
            TransporterSource();
            CommandSource();
            LaunchRuntime(harmony);
        }
        catch (Exception ex) { Console.WriteLine(ex.StackTrace); throw; }
        finally { harmony.UnpatchAll(harmony.Id); CultureInfo.CurrentCulture = culture; Clear(); }
    }

    private static void CommandSource()
    {
        Reset();
        var source = new Source(); var command = new ClientRaidCommandSource(source);
        Check(command.IsCurrent && command.IsAvailable && command.PawnCount == 1, "Command source lost engine availability/count.");
        Check(command.CreateLaunch().TryCapture(context, Target(), out var manifest, out _)
            && manifest!.SourceLoadId == "original" && source.CargoContext == ("user-é", "colony", "base"),
            "Command launch did not reuse the captured source adapter.");
        world = Uninitialized<World>();
        Check(!command.IsCurrent, "Command source survived world replacement in the same game.");
        command = new(source); game = Uninitialized<Game>();
        Check(!command.IsCurrent, "Command source survived game replacement.");
        var missing = ClientRaidCommandSource.ForCaravan(null);
        Check(missing.IsCurrent && !missing.IsAvailable && missing.PawnCount == 0, "Missing vehicle caravan was not represented as unavailable.");
        var caravanSource = ClientRaidCommandSource.ForCaravan(caravan);
        Check(caravanSource.IsAvailable, "Existing caravan was unavailable.");
        caravanDestroyed = true;
        Check(!caravanSource.IsAvailable, "Command source ignored destroyed caravan.");
    }
    private static void CaptureAndRevalidate()
    {
        Reset();
        var source = new Source(); var force = new ClientRaidAttackForce(source);
        Check(force.TryCapture(context, Target(), out var manifest, out var reason) && reason == "", "Valid force rejected.");
        Check(manifest!.SourceLoadId == "original" && manifest.PawnGlobalKeys.SequenceEqual(new[] { "owner:user-é/pawn:Human42" }), "Captured identity changed.");
        Check(source.CargoContext == ("user-é", "colony", "base"), "Cargo used another context.");
        source.Id = "changed";
        source.Cargo[0].Metadata["complex"] = "changed";
        source.Cargo[0].ThingPackage!.Scribe!.XmlGzipBase64 = "changed";
        source.Cargo[0].UniqueWeaponTraits.Add("changed"); source.Cargo.Clear();
        Check(manifest.SourceLoadId == "original" && manifest.CarriedThings[0].Metadata["complex"] == "state-é"
            && manifest.CarriedThings[0].ThingPackage!.Scribe!.XmlGzipBase64 == "packed"
            && manifest.CarriedThings[0].UniqueWeaponTraits.SequenceEqual(new[] { "trait" }), "Manifest aliases mutable source state.");
        Check(force.EntryFailure() is null, "Stable force failed revalidation.");
        Check(!force.TryEnterBattle(new() { EventId = "raid" }, null, out reason)
            && reason == "ClashOfRim.Raid.StatusMissingPreparedBattleMap" && trace.Count == 0, "Missing prepared map fell back or mutated game.");

        foreach (string invalid in new[] { "game", "source", "tile", "layer", "missing", "dead", "destroyed" })
        {
            Reset(); source = new(); force = new(source);
            Check(force.TryCapture(context, Target(), out _, out _), "Fixture capture failed.");
            switch (invalid)
            {
                case "game": game = Uninitialized<Game>(); break;
                case "source": source.Available = false; break;
                case "tile": source.CurrentTile++; break;
                case "layer": source.Layer++; break;
                case "missing": source.Pawns.Clear(); break;
                case "dead": dead.Add(source.Pawns[0]); break;
                case "destroyed": destroyed.Add(source.Pawns[0]); break;
            }
            Check(force.EntryFailure() is not null && !force.TryEnterBattle(new(), map, out reason) && reason.Length != 0
                && trace.Count == 0 && registered is null, "Stale force reached battlefield: " + invalid);
        }
        foreach (string invalid in new[] { "game", "source", "tile", "layer", "empty" })
        {
            Reset(); source = new(); force = new(source);
            switch (invalid)
            {
                case "game": game = Uninitialized<Game>(); break;
                case "source": source.Available = false; break;
                case "tile": source.CurrentTile++; break;
                case "layer": source.Layer++; break;
                case "empty": source.Pawns.Clear(); break;
            }
            Check(!force.TryCapture(context, Target(), out manifest, out reason) && manifest is null && reason.Length != 0
                && source.CargoContext is null && trace.Count == 0, "Invalid capture read cargo or touched engine: " + invalid);
        }
        Reset(); source = new(); force = new(source);
        Check(force.EntryFailure() is not null, "Uncaptured force admitted.");
        Check(force.TryCapture(context, Target(), out _, out _), "Initial capture failed.");
        source.Pawns.Clear();
        Check(!force.TryCapture(context, Target(), out manifest, out _) && manifest is null && force.EntryFailure() is not null, "Failed recapture retained old manifest.");
    }

    private static void Battlefield()
    {
        foreach (bool proxy in new[] { false, true })
        foreach (string outcome in new[] { "compatibility", "default", "compatibility-failure", "placement-failure", "placement-exception" })
        {
            Reset(); defender = proxy ? Uninitialized<Faction>() : null;
            var source = new Source
            {
                Entry = outcome == "compatibility" ? RaidCaravanMapEntryResult.Success(new[] { "compat-pawn" })
                    : outcome == "compatibility-failure" ? RaidCaravanMapEntryResult.Failed("compat-error") : RaidCaravanMapEntryResult.NotHandled,
                Place = outcome != "placement-failure", Throw = outcome == "placement-exception"
            };
            var force = new ClientRaidAttackForce(source);
            Check(force.TryCapture(context, Target(), out _, out _), "Battle fixture capture failed.");
            var session = new ActiveRaidBattleSession { EventId = "raid", DefenderUserId = "defender" };
            bool accepted = false; string reason = ""; bool threw = false;
            try { accepted = force.TryEnterBattle(session, map, out reason); }
            catch (InvalidOperationException) when (source.Throw) { threw = true; }
            bool success = outcome is "compatibility" or "default";
            var expected = new List<string> { "proxy" };
            if (proxy) expected.AddRange(new[] { "relation", "relation", "area", "construction", "drop-carried", "forbid", "convert", "loot", "ai", "notify", "guards" });
            expected.Add("compatibility");
            if (outcome is not ("compatibility" or "compatibility-failure")) expected.Add("place");
            if (success) expected.AddRange(new[] { "cleanup", "register" });
            Check(accepted == success && threw == source.Throw && trace.SequenceEqual(expected), "Battlefield order/dispatch changed: " + outcome + ": " + string.Join(",", trace));
            Check(ReferenceEquals(registered, success ? session : null), "Battle registered on failure or wrong session.");
            if (success) Check(session.AttackPawnThingIds.SequenceEqual(new[] { outcome == "compatibility" ? "compat-pawn" : "Human42" }), "Battle pawn receipt changed.");
            if (outcome == "compatibility-failure") Check(reason == "compat-error", "Compatibility reason lost.");
            if (outcome == "placement-failure") Check(reason == "place-error", "Placement reason lost.");
        }
    }

    private static void CaravanSource()
    {
        Reset(); var source = new CaravanRaidAttackForceSource(caravan);
        Pawn pawn = NewPawn(42), notOwned = NewPawn(43), deadPawn = NewPawn(44), destroyedPawn = NewPawn(45);
        members.AddRange(new[] { pawn, notOwned, deadPawn, destroyedPawn });
        nonOwners.Add(notOwned); dead.Add(deadPawn); destroyed.Add(destroyedPawn);
        Check(source.IsAvailable && source.Tile == 7 && source.TileLayerId == 2 && source.UniqueLoadId == "caravan-original"
            && source.AttackPawns.SequenceEqual(new[] { pawn }), "Caravan source selection or location changed.");
        var force = new ClientRaidAttackForce(source);
        Check(force.TryCapture(context, Target(), out var manifest, out _) && manifest!.CarriedThings.Single().GlobalKey == "caravan-cargo", "Caravan did not reuse delivered-reference builder.");
        nonOwners.Add(pawn);
        Check(force.EntryFailure() is not null && trace.Count == 0, "Lost caravan ownership admitted after capture.");
        nonOwners.Remove(pawn);
        var session = new ActiveRaidBattleSession();
        Check(source.TryPlaceAttackersOnMap(session, map, new[] { new RaidAttackPawnLoad("global", pawn) }, out var reason)
            && reason == "" && session.AttackPawnThingIds.SequenceEqual(new[] { "Human42" }) && !members.Contains(pawn)
            && trace.SequenceEqual(new[] { "edge", "remove-pawn", "spawn" }), "Caravan placement failed or changed mutation ordering.");
        trace.Clear(); source.CleanupAfterSuccessfulEntry();
        Check(trace.Count == 0 && caravanInWorld, "Non-empty source removed.");
        members.Clear(); source.CleanupAfterSuccessfulEntry(); source.CleanupAfterSuccessfulEntry();
        Check(trace.SequenceEqual(new[] { "remove-caravan" }) && !caravanInWorld, "Empty caravan cleanup missing or repeated.");
        trace.Clear(); caravanDestroyed = true; source.CleanupAfterSuccessfulEntry();
        Check(!source.IsAvailable && trace.Count == 0, "Destroyed caravan cleaned twice.");
        caravanDestroyed = false;
        Check(!source.TryPlaceAttackersOnMap(new(), map, new[] { new RaidAttackPawnLoad("missing", pawn) }, out reason)
            && reason == "ClashOfRim.Raid.StatusAttackForcePlacementFailed" && !trace.Contains("spawn"), "Missing caravan pawn spawned.");
    }

    private static void TransporterSource()
    {
        foreach (bool validLanding in new[] { false, true })
        {
            Reset(); landingValid = validLanding;
            Pawn pawn = NewPawn(42), prisoner = NewPawn(43), foreign = NewPawn(44), deadPawn = NewPawn(45);
            prisoners.Add(prisoner); nonOwners.Add(foreign); dead.Add(deadPawn);
            var cargo = new ThingWithComps { def = new ThingDef { defName = "Rifle" }, thingIDNumber = 50, stackCount = 2 };
            var coding = new CompBiocodable { parent = cargo };
            AccessTools.Field(typeof(CompBiocodable), "codedPawn").SetValue(coding, pawn);
            AccessTools.Field(typeof(CompBiocodable), "biocoded").SetValue(coding, true);
            AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(cargo, new List<ThingComp> { coding });
            Thing shuttle = NewThing("Shuttle", 51), rejected = NewThing("Rejected", 52), broken = NewThing("Rifle", 53);
            destroyed.Add(broken);
            var pod = new ActiveTransporterInfo();
            ((ThingOwner<Thing>)pod.innerContainer).InnerListForReading.AddRange(new Thing[] { pawn, prisoner, foreign, deadPawn, cargo, rejected, broken });
            var ship = new ActiveTransporterInfo(); ((ThingOwner<Thing>)ship.innerContainer).InnerListForReading.Add(shuttle); shuttles[ship] = shuttle;
            var inputs = new List<ActiveTransporterInfo> { pod, ship, null! };
            var source = new TransporterRaidAttackForceSource(inputs, 7, "transport-é", 2); inputs.Clear();
            Check(source.IsAvailable && source.Tile == 7 && source.TileLayerId == 2 && source.UniqueLoadId == "transporters:transport-é"
                && source.AttackPawns.SequenceEqual(new[] { pawn }), "Transport source did not preserve selected containers or excluded pawns.");
            var refs = source.BuildCarriedThings("current", "colony", "snapshot");
            Check(refs.Count == 1 && referenced.SequenceEqual(new[] { cargo })
                && refs[0].GlobalKey == "owner:current/colony:colony/snapshot:snapshot/transportPods:transport-é/thing:Rifle50"
                && refs[0].BiocodedPawnGlobalId == "owner:current/pawn:Human42", "Transport cargo used stale identity or included shuttle/non-tradeable payload.");
            Check(source.TryEnterMapViaCompatibility(map, new[] { pawn }).Kind == RaidCaravanMapEntryResultKind.NotHandled, "Transport invoked caravan compatibility.");
            var session = new ActiveRaidBattleSession();
            Check(source.TryPlaceAttackersOnMap(session, map, new[] { new RaidAttackPawnLoad("global", pawn), new RaidAttackPawnLoad("global", pawn) }, out var reason)
                && reason == "" && session.AttackPawnThingIds.SequenceEqual(new[] { "Human42" }) && landed.SequenceEqual(new[] { ship, pod }), "Mixed shuttle/pod dispatch or pawn deduplication changed.");
            Check(trace.SequenceEqual(validLanding ? new[] { "landing", "shuttle", "pods" } : new[] { "landing", "edge", "shuttle", "pods" }), "Transport landing fallback changed.");
        }
        Reset(); var empty = new TransporterRaidAttackForceSource(new(), 7, "", 2);
        string generatedId = empty.UniqueLoadId;
        Check(!empty.IsAvailable && empty.AttackPawns.Count == 0 && generatedId.StartsWith("transporters:", StringComparison.Ordinal)
            && Guid.TryParseExact(generatedId.Substring("transporters:".Length), "N", out _) && empty.UniqueLoadId == generatedId
            && !empty.TryPlaceAttackersOnMap(new(), map, Array.Empty<RaidAttackPawnLoad>(), out _) && trace.Count == 0, "Empty transport reached engine effects.");
    }

    private sealed class Source : IRaidAttackForceSource
    {
        internal bool Available = true, Place = true, Throw;
        internal int CurrentTile = 7, Layer = 2;
        internal string Id = "original";
        internal readonly List<Pawn> Pawns = new() { NewPawn(42) };
        internal readonly List<ModThingReferenceDto> Cargo = new() { new() { GlobalKey = "cargo", Metadata = new() { ["complex"] = "state-é" }, UniqueWeaponTraits = new() { "trait" }, ThingPackage = new() { Scribe = new() { XmlGzipBase64 = "packed" } } } };
        internal (string, string, string)? CargoContext;
        internal RaidCaravanMapEntryResult Entry = RaidCaravanMapEntryResult.NotHandled;
        public bool IsAvailable => Available;
        public int Tile => CurrentTile;
        public int TileLayerId => Layer;
        public string UniqueLoadId => Id;
        public IReadOnlyList<Pawn> AttackPawns => Pawns;
        public IReadOnlyList<ModThingReferenceDto> BuildCarriedThings(string user, string colony, string snapshot) { CargoContext = (user, colony, snapshot); return Cargo; }
        public RaidCaravanMapEntryResult TryEnterMapViaCompatibility(Map value, IReadOnlyList<Pawn> pawns)
        { Check(ReferenceEquals(value, map) && pawns.SequenceEqual(Pawns), "Compatibility received wrong map/force."); trace.Add("compatibility"); return Entry; }
        public bool TryPlaceAttackersOnMap(ActiveRaidBattleSession session, Map value, IReadOnlyList<RaidAttackPawnLoad> pawns, out string failure)
        {
            Check(ReferenceEquals(value, map) && pawns.Select(p => p.Pawn).SequenceEqual(Pawns), "Placement received wrong map/force.");
            trace.Add("place"); if (Throw) throw new InvalidOperationException("placement");
            failure = Place ? "" : "place-error"; if (Place) session.AttackPawnThingIds.AddRange(pawns.Select(p => p.Pawn.ThingID)); return Place;
        }
        public void CleanupAfterSuccessfulEntry() => trace.Add("cleanup");
    }

    private static RemoteSessionMapOpenRequest? openedRequest;
    private static RemoteSessionMapParent? battleParent;
    private static bool failOpen, missingMap;
    private static void LaunchRuntime(Harmony harmony)
    {
        void Patch(Type type, string method, string prefix) => harmony.Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(typeof(ClientRaidAttackForceTests), prefix));
        Patch(typeof(RemoteSessionMapUtility), nameof(RemoteSessionMapUtility.TryOpen), nameof(OpenMap));
        Patch(typeof(RemoteSessionMapUtility), nameof(RemoteSessionMapUtility.FindActiveSessionMap), nameof(FindBattle));
        harmony.Patch(AccessTools.PropertyGetter(typeof(ClashOfRimGameComponent), nameof(ClashOfRimGameComponent.ActiveRaidBattleSession)),
            prefix: new HarmonyMethod(typeof(ClientRaidAttackForceTests), nameof(ActiveBattle)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(MapParent), nameof(MapParent.Map)), prefix: new HarmonyMethod(typeof(ClientRaidAttackForceTests), nameof(BattleMap)));
        foreach (bool failed in new[] { false, true })
        {
            Reset(); openedRequest = null; failOpen = failed; missingMap = false; battleParent = Uninitialized<RemoteSessionMapParent>(); map.uniqueID = 91;
            var runtime = new ClientRaidLaunchRuntime(new Source());
            var target = Target(); target.OwnerUserId = "defender"; target.OwnerColonyId = "defender-colony"; target.MapId = "Map_source"; target.WorldObjectId = "world";
            Check(runtime.TryCapture(context, target, out _, out _) && runtime.EntryFailure() is null && !runtime.HasBattle, "Runtime capture/pre-open state incorrect.");
            var snapshot = new RemoteSnapshotOpenPayload("defender-snapshot", new() { NextLineageToken = "defender-lineage" }, new byte[] { 1, 2, 3 });
            var prepared = new ClientPreparedRaid("raid", "authorization", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2),
                new() { ContractId = "guard", Tier = "elite", Points = 91, Seed = 7 }, snapshot);
            string? reason = runtime.Open(context, target, prepared);
            Check(openedRequest is { Mode: "RaidBattle", SnapshotId: "defender-snapshot", RelatedEventId: "raid", CloseExistingObservationSessions: true }
                && ReferenceEquals(openedRequest.Target, target) && ReferenceEquals(openedRequest.Package, snapshot.Package)
                && ReferenceEquals(openedRequest.Payload, snapshot.Payload), "Runtime changed map open request.");
            if (failed)
            { Check(reason == "map failed" && registered is null && !runtime.HasBattle && trace.Count == 0, "Failed map reached pawn transfer/registration."); continue; }
            Check(reason is null && runtime.HasBattle && registered is { EventId: "raid", AttackerUserId: "user-é", AttackerColonyId: "colony", AttackerSnapshotId: "base",
                DefenderUserId: "defender", DefenderColonyId: "defender-colony", DefenderSnapshotId: "defender-snapshot", DefenderLineageToken: "defender-lineage",
                TargetMapId: "Map_source", ClientMapId: "Map_91", TargetTile: 7, GuardDeploymentId: "guard", GuardDeploymentPoints: 91, GuardDeploymentSeed: 7 }, "Runtime lost battle identity/guard/source mapping.");
            Check(registered!.DeadlineUtcTicks - registered.StartedAtUtcTicks == TimeSpan.FromMinutes(15).Ticks
                && registered.FinalDeadlineUtcTicks - registered.DeadlineUtcTicks == TimeSpan.FromMinutes(2).Ticks, "Runtime changed fallback deadlines.");
            long previousDeadline = registered.DeadlineUtcTicks; runtime.ApplyReceipt("accepted", null);
            Check(registered.AttackerSnapshotId == "accepted" && registered.DeadlineUtcTicks == previousDeadline, "Missing server deadlines erased local fallback.");
            runtime.ApplyReceipt("next", new(10, 20, 30));
            Check(registered.AttackerSnapshotId == "next" && registered.StartedAtUtcTicks == 10 && registered.DeadlineUtcTicks == 20 && registered.FinalDeadlineUtcTicks == 30, "Server receipt not applied to owned battle.");
            missingMap = true; Check(!runtime.HasBattle, "Missing map accepted for snapshot."); missingMap = false;
            registered = new(); Check(!runtime.HasBattle, "Replacement battle borrowed by old runtime.");
        }
    }
    private static bool OpenMap(RemoteSessionMapOpenRequest __0, out RemoteSessionMapOpenResult result, ref bool __result)
    { openedRequest = __0; result = failOpen ? RemoteSessionMapOpenResult.Failed("map failed") : RemoteSessionMapOpenResult.Opened(battleParent, map); __result = !failOpen; return false; }
    private static bool FindBattle(ActiveRemoteMapSession __0, ref RemoteSessionMapParent? __result)
    { Check(__0.RelatedEventId == "raid", "Wrong battle lookup."); __result = missingMap ? null : battleParent; return false; }
    private static bool ActiveBattle(ref ActiveRaidBattleSession? __result) { __result = registered; return false; }
    private static bool BattleMap(ref Map? __result) { __result = map; return false; }

    private static void Install(Harmony harmony)
    {
        void Patch(Type type, string method, string prefix) => harmony.Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(typeof(ClientRaidAttackForceTests), prefix));
        void Getter(Type type, string property, string prefix) => harmony.Patch(AccessTools.PropertyGetter(type, property), prefix: new HarmonyMethod(typeof(ClientRaidAttackForceTests), prefix));
        Getter(typeof(Current), nameof(Current.Game), nameof(Game));
        Getter(typeof(Find), nameof(Find.World), nameof(CurrentWorld));
        Getter(typeof(Pawn), nameof(Pawn.Dead), nameof(Dead));
        Getter(typeof(Thing), nameof(Thing.Destroyed), nameof(Destroyed));
        Getter(typeof(Thing), nameof(Thing.Faction), nameof(ThingFaction));
        Getter(typeof(Pawn), nameof(Pawn.IsPrisonerOfColony), nameof(Prisoner));
        Getter(typeof(Faction), nameof(Faction.OfPlayer), nameof(Player));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClashOfRimText)).Where(m => m.Name == nameof(ClashOfRimText.Key)))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientRaidAttackForceTests), nameof(Text)));
        Patch(typeof(ClashLog), nameof(ClashLog.Message), nameof(Skip));
        Patch(typeof(Log), nameof(Log.Warning), nameof(Skip));
        Patch(typeof(PlayerFactionProxyUtility), nameof(PlayerFactionProxyUtility.EnsureProxyForUser), nameof(Proxy));
        Patch(typeof(Faction), nameof(Faction.SetRelationDirect), nameof(Relation));
        Patch(typeof(RemoteSessionMapAreaSanitizer), nameof(RemoteSessionMapAreaSanitizer.Apply), nameof(MapEffect));
        Patch(typeof(RemoteSessionMapConstructionSanitizer), nameof(RemoteSessionMapConstructionSanitizer.Apply), nameof(MapEffect));
        Patch(typeof(RemoteSessionPawnStateSanitizer), nameof(RemoteSessionPawnStateSanitizer.DropLoadedCarriedThings), nameof(MapEffect));
        Patch(typeof(RemoteSessionMapThingAccessSanitizer), nameof(RemoteSessionMapThingAccessSanitizer.ForbidLoadedHaulables), nameof(MapEffect));
        Patch(typeof(RaidDefenderMapUtility), nameof(RaidDefenderMapUtility.ConvertPlayerOwnedMapObjectsToDefenderProxy), nameof(MapEffect));
        Patch(typeof(RaidDefenderLootProtectionUtility), nameof(RaidDefenderLootProtectionUtility.Apply), nameof(MapEffect));
        Patch(typeof(DefensePointRaidAiApplicator), nameof(DefensePointRaidAiApplicator.Apply), nameof(MapEffect));
        Patch(typeof(ClashOfRimCompatibilityApi), nameof(ClashOfRimCompatibilityApi.NotifyRemoteDefenderMapPrepared), nameof(MapEffect));
        Patch(typeof(RaidGuardDeploymentUtility), nameof(RaidGuardDeploymentUtility.DeployIfNeeded), nameof(MapEffect));
        Patch(typeof(ClashOfRimGameComponent), nameof(ClashOfRimGameComponent.SetActiveRaidBattleSession), nameof(Register));
        Getter(typeof(WorldObject), nameof(WorldObject.Tile), nameof(Tile));
        Getter(typeof(WorldObject), nameof(WorldObject.Destroyed), nameof(CaravanDestroyed));
        Patch(typeof(WorldObject), nameof(WorldObject.GetUniqueLoadID), nameof(LoadId));
        Patch(typeof(RemoteWorldObjectUiUtility), nameof(RemoteWorldObjectUiUtility.ReadTileLayerId), nameof(Layer));
        Getter(typeof(Caravan), nameof(Caravan.PawnsListForReading), nameof(Members));
        Patch(typeof(Caravan), nameof(Caravan.IsOwner), nameof(Owner));
        Patch(typeof(Caravan), nameof(Caravan.ContainsPawn), nameof(Contains));
        Patch(typeof(Caravan), nameof(Caravan.RemovePawn), nameof(RemovePawn));
        Patch(typeof(TradeCaravanFulfillmentUtility), nameof(TradeCaravanFulfillmentUtility.BuildDeliveredThingReferences), nameof(CaravanCargo));
        Patch(typeof(PawnExchangePlacementService), nameof(PawnExchangePlacementService.FindMapEdgeLandingCell), nameof(Edge));
        Patch(typeof(PawnExchangePlacementService), nameof(PawnExchangePlacementService.SpawnNearMapEdge), nameof(Spawn));
        Getter(typeof(Find), nameof(Find.WorldObjects), nameof(WorldObjects));
        Patch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Contains), nameof(ContainsCaravan));
        Patch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Remove), nameof(RemoveCaravan));
        Patch(typeof(ActiveTransporterInfo), nameof(ActiveTransporterInfo.GetShuttle), nameof(Shuttle));
        Patch(typeof(TradeThingReferenceUtility), nameof(TradeThingReferenceUtility.IsTradeableItem), nameof(Tradeable));
        Patch(typeof(TradeThingReferenceUtility), nameof(TradeThingReferenceUtility.BuildThingReference), nameof(Reference));
        harmony.Patch(AccessTools.Method(typeof(DropCellFinder), nameof(DropCellFinder.GetBestShuttleLandingSpot), new[] { typeof(Map), typeof(Faction) }),
            prefix: new HarmonyMethod(typeof(ClientRaidAttackForceTests), nameof(Landing)));
        Patch(typeof(TransportersArrivalActionUtility), nameof(TransportersArrivalActionUtility.DropShuttle), nameof(DropShuttle));
        Patch(typeof(TransportersArrivalActionUtility), nameof(TransportersArrivalActionUtility.DropTravellingDropPods), nameof(DropPods));
    }

    private static void Clear() { destroyed.Clear(); dead.Clear(); nonOwners.Clear(); prisoners.Clear(); trace.Clear(); members.Clear(); shuttles.Clear(); referenced.Clear(); landed.Clear(); registered = null; }
    private static void Reset() { Clear(); game = Uninitialized<Game>(); world = Uninitialized<World>(); map = Uninitialized<Map>(); player = Uninitialized<Faction>(); defender = null; caravan = Uninitialized<Caravan>(); caravanDestroyed = false; caravanInWorld = true; tile = 7; layer = 2; }
    private static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static Pawn NewPawn(int id) => new() { def = new ThingDef { defName = "Human" }, thingIDNumber = id };
    private static Thing NewThing(string name, int id) => new() { def = new ThingDef { defName = name }, thingIDNumber = id, stackCount = 2 };
    private static ModWorldMapMarkerDto Target() => new() { Tile = 7, TileLayerId = 2 };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static bool Skip() => false;
    private static bool Text(string __0, ref string __result) { __result = __0; return false; }
    private static bool Game(ref Game __result) { __result = game; return false; }
    private static bool CurrentWorld(ref World __result) { __result = world; return false; }
    private static bool Dead(Pawn __instance, ref bool __result) { __result = dead.Contains(__instance); return false; }
    private static bool Destroyed(Thing __instance, ref bool __result) { __result = destroyed.Contains(__instance); return false; }
    private static bool ThingFaction(Thing __instance, ref Faction? __result) { __result = __instance is Pawn p && nonOwners.Contains(p) ? null : player; return false; }
    private static bool Prisoner(Pawn __instance, ref bool __result) { __result = prisoners.Contains(__instance); return false; }
    private static bool Player(ref Faction __result) { __result = player; return false; }
    private static bool Proxy(string __0, ref Faction? __result) { Check(__0 == "defender", "Wrong defender identity."); trace.Add("proxy"); __result = defender; return false; }
    private static bool Relation(Faction __instance, Faction __0, FactionRelationKind __1)
    { Check(__1 == FactionRelationKind.Hostile && (ReferenceEquals(__instance, player) ? ReferenceEquals(__0, defender) : ReferenceEquals(__0, player)), "Wrong hostility relationship."); trace.Add("relation"); return false; }
    private static bool MapEffect(MethodBase __originalMethod, object[] __args)
    {
        Check(ReferenceEquals(__args[0], map), "Sanitizer received wrong map.");
        if (__args.Length > 1 && __args[1] is Faction) Check(ReferenceEquals(__args[1], defender), "Wrong defender faction.");
        trace.Add(__originalMethod.DeclaringType!.Name switch
        {
            nameof(RemoteSessionMapAreaSanitizer) => "area", nameof(RemoteSessionMapConstructionSanitizer) => "construction",
            nameof(RemoteSessionPawnStateSanitizer) => "drop-carried", nameof(RemoteSessionMapThingAccessSanitizer) => "forbid",
            nameof(RaidDefenderMapUtility) => "convert", nameof(RaidDefenderLootProtectionUtility) => "loot",
            nameof(DefensePointRaidAiApplicator) => "ai", nameof(ClashOfRimCompatibilityApi) => "notify", nameof(RaidGuardDeploymentUtility) => "guards",
            _ => throw new Exception("Unexpected map effect.")
        });
        return false;
    }
    private static bool Register(ActiveRaidBattleSession __0) { Check(registered is null, "Battle registered twice."); registered = __0; trace.Add("register"); return false; }
    private static bool Tile(ref PlanetTile __result) { __result = new(tile, layer); return false; }
    private static bool CaravanDestroyed(ref bool __result) { __result = caravanDestroyed; return false; }
    private static bool LoadId(ref string __result) { __result = "caravan-original"; return false; }
    private static bool Layer(ref int __result) { __result = layer; return false; }
    private static bool Members(ref List<Pawn> __result) { __result = members; return false; }
    private static bool Owner(Pawn __0, ref bool __result) { __result = !nonOwners.Contains(__0); return false; }
    private static bool Contains(Pawn __0, ref bool __result) { __result = members.Contains(__0); return false; }
    private static bool RemovePawn(Pawn __0) { Check(members.Remove(__0), "Removed absent pawn."); trace.Add("remove-pawn"); return false; }
    private static bool CaravanCargo(Caravan __0, string __1, string __2, string __3, ref IReadOnlyList<ModThingReferenceDto> __result)
    { Check(ReferenceEquals(__0, caravan) && (__1, __2, __3) == (context.UserId, context.ColonyId, context.CurrentSnapshotId), "Wrong caravan cargo context."); __result = new[] { new ModThingReferenceDto { GlobalKey = "caravan-cargo" } }; return false; }
    private static bool Edge(Map __0, ref IntVec3 __result) { Check(ReferenceEquals(__0, map), "Wrong edge map."); trace.Add("edge"); __result = edge; return false; }
    private static bool Spawn(Pawn __0, Map __1, IntVec3 __2, int __3, ref IntVec3 __result)
    { Check(!members.Contains(__0) && ReferenceEquals(__1, map) && __2 == edge && __3 == 4, "Spawn preceded removal or changed location/radius."); trace.Add("spawn"); __result = edge; return false; }
    private static bool WorldObjects(ref WorldObjectsHolder __result) { __result = Uninitialized<WorldObjectsHolder>(); return false; }
    private static bool ContainsCaravan(WorldObject __0, ref bool __result) { Check(ReferenceEquals(__0, caravan), "Wrong source queried."); __result = caravanInWorld; return false; }
    private static bool RemoveCaravan(WorldObject __0) { Check(ReferenceEquals(__0, caravan) && members.Count == 0 && caravanInWorld, "Removed nonempty/absent source."); caravanInWorld = false; trace.Add("remove-caravan"); return false; }
    private static bool Shuttle(ActiveTransporterInfo __instance, ref Thing? __result) { shuttles.TryGetValue(__instance, out __result); return false; }
    private static bool Tradeable(Thing __0, ref bool __result) { __result = __0.def.defName != "Rejected"; return false; }
    private static bool Reference(Thing __0, string __1, int __2, string? __3, ref ModThingReferenceDto __result)
    { referenced.Add(__0); __result = new() { GlobalKey = __1, StackCount = __2, BiocodedPawnGlobalId = __3 }; return false; }
    private static bool Landing(Map __0, ref IntVec3 __result) { Check(ReferenceEquals(__0, map), "Wrong landing map."); trace.Add("landing"); __result = landingValid ? landing : IntVec3.Invalid; return false; }
    private static bool DropShuttle(ActiveTransporterInfo __0, Map __1, IntVec3 __2)
    { Check(ReferenceEquals(__1, map) && __2 == (landingValid ? landing : edge) && shuttles.ContainsKey(__0), "Wrong shuttle landing."); landed.Add(__0); trace.Add("shuttle"); return false; }
    private static bool DropPods(List<ActiveTransporterInfo> __0, IntVec3 __1, Map __2)
    { Check(ReferenceEquals(__2, map) && __1 == (landingValid ? landing : edge) && __0.All(p => !shuttles.ContainsKey(p)), "Wrong drop pod landing."); landed.AddRange(__0); trace.Add("pods"); return false; }
}
