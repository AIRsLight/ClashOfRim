using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class RemoteTransporterActionsTests
{
    private static bool baseAccepted = true, cargoAccepted = true;
    private static int giftCargoCalls, raidCargoCalls, sentGifts, startedRaids;
    private static string? surface, sentKey;
    private static object[]? sentGift;
    private static ModWorldMapMarkerDto? raidTarget;
    private static IRaidAttackForceSource? raidForce;
    private static readonly List<ActiveTransporterInfo> cargo = new() { new ActiveTransporterInfo() };

    internal static void Run()
    {
        var previous = ClientTransporterActions.Current; var culture = CultureInfo.CurrentCulture;
        var harmony = new Harmony("ClashOfRim.Tests.RemoteTransporters");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(RemoteTransporterActionsTests), prefix));
        Patch(AccessTools.Method(typeof(TransportersArrivalAction), nameof(TransportersArrivalAction.StillValid)), nameof(BaseValid));
        Patch(AccessTools.Method(typeof(GiftTransporterPayloadUtility), nameof(GiftTransporterPayloadUtility.CanSend)), nameof(GiftCargo));
        Patch(AccessTools.Method(typeof(TransportersArrivalActionUtility), nameof(TransportersArrivalActionUtility.AnyNonDownedColonist)), nameof(RaidCargo));
        Patch(AccessTools.Method(typeof(ClientGiftActions), nameof(ClientGiftActions.SendTransportPods)), nameof(SendGift));
        Patch(AccessTools.Method(typeof(ClientRaidCommands), nameof(ClientRaidCommands.StartTransporters)), nameof(StartRaid));
        Patch(AccessTools.Method(typeof(RemoteWorldObjectUiUtility), nameof(RemoteWorldObjectUiUtility.ReadTileLayerId)), nameof(Layer));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.World)), nameof(World));
        Patch(AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.LogVerbose)), nameof(Inactive));
        Patch(AccessTools.PropertyGetter(typeof(GenTypes), "AllActiveAssemblies"), nameof(Assemblies));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClashOfRimText)).Where(m => m.Name == nameof(ClashOfRimText.Key))) Patch(method, nameof(Text));
        foreach (var method in typeof(Messages).GetMethods().Where(m => m.Name == nameof(Messages.Message))) Patch(method, nameof(Quiet));
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                var gifts = new ClientGiftModuleTests.Fixture(ClientGiftSourceKind.TransportPods);
                var raids = new ClientRaidCommandTests.Fixture();
                var giftActions = new ClientGiftActions(gifts.Gifts, gifts.Session, gifts.Ports, gifts,
                    () => gifts.Capabilities, () => Array.Empty<ModWorldMapMarkerDto>());
                var actions = new ClientTransporterActions(giftActions, raids.Commands, () => gifts.Capabilities);
                ClientTransporterActions.Attach(actions);
                Matrix(gifts, actions); SerializationAndDispatch(gifts); ArrivalGuard(gifts);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        finally
        {
            Scribe.ForceStop(); harmony.UnpatchAll(harmony.Id); ClientTransporterActions.Attach(previous);
            CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset();
        }
    }

    private static void Matrix(ClientGiftModuleTests.Fixture f, ClientTransporterActions actions)
    {
        foreach (bool gifts in new[] { false, true })
        foreach (bool pvp in new[] { false, true })
        foreach (bool raidable in new[] { false, true })
        foreach (string kind in new[] { "TradeableColony", "ActiveRaidTarget", "RuntimeCaravan" })
        foreach (string missing in new[] { "", "user", "colony", "map", "world", "snapshot" })
        {
            f.Capabilities = new(gifts, pvp);
            var view = new View { RuntimeKind = kind, CanRaid = raidable,
                OwnerUserId = missing == "user" ? " " : "owner", OwnerColonyId = missing == "colony" ? "" : "colony",
                SourceMapId = missing == "map" ? "" : "map", SourceWorldObjectId = missing == "world" ? "" : "world",
                SourceSnapshotId = missing == "snapshot" ? "" : "snapshot" };
            var target = RemoteWorldObjectUiUtility.CaptureTarget(view);
            bool complete = missing is not ("user" or "colony" or "map");
            string? raidFailure = !pvp ? "ClashOfRim.Raid.PvpDisabled" : !complete || missing == "world" || !raidable ? "ClashOfRim.Raid.StatusTargetIncomplete" : null;
            Check(ClientRaidTargets.Rejection(target, pvp) == raidFailure, "Raid admission changed target requirements.");
            Check(Menu<bool>("CanReceiveTransportPodRaid", view) == (raidFailure is null), "Raid menu diverged from launch policy.");
            giftCargoCalls = raidCargoCalls = 0;
            AssertReport(actions.RaidAcceptance(target, cargo), raidFailure);
            AssertReport(new RemoteRaidTransportersArrivalAction(view).StillValid(cargo, view.Tile), raidFailure);
            Check(raidCargoCalls == (raidFailure is null ? 2 : 0), "Raid cargo was checked before target/policy admission.");
            foreach (bool forced in new[] { false, true })
            {
                bool offered = complete && kind == "TradeableColony" && (!forced || pvp && raidable);
                string? arrivalFailure = !gifts ? "ClashOfRim.GiftDelivery.Disabled" : forced && !pvp ? "ClashOfRim.Raid.PvpDisabled"
                    : !complete ? "ClashOfRim.GiftDelivery.StatusTargetIncomplete" : null;
                string? menuFailure = arrivalFailure ?? (!offered ? "ClashOfRim.GiftDelivery.StatusTargetIncomplete" : null);
                Check(Menu<bool>("CanReceiveTransportPodDelivery", view, forced) == offered, "Gift menu changed ordinary/forced visibility.");
                giftCargoCalls = 0; surface = null;
                AssertReport(Menu<FloatMenuAcceptanceReport>("CanSendTransportPodDelivery", view, cargo, forced), menuFailure);
                AssertReport(new RemoteGiftTransportersArrivalAction(view, forced).StillValid(cargo, view.Tile), arrivalFailure);
                Check(giftCargoCalls == (menuFailure is null ? 1 : 0) + (arrivalFailure is null ? 1 : 0), "Gift cargo bypassed policy or was redundantly checked.");
                if (giftCargoCalls > 0) Check(surface == (forced ? ThingReferenceSurfaces.ForcedDelivery : ThingReferenceSurfaces.Gift), "Wrong gift transfer surface.");
            }
        }
        f.Capabilities = new(true, true);
        var valid = RemoteWorldObjectUiUtility.CaptureTarget(new View());
        raidCargoCalls = 0;
        AssertReport(actions.RaidAcceptance(valid, Array.Empty<IThingHolder>()), "ClashOfRim.Raid.TransportPodRejectNoColonist");
        Check(raidCargoCalls == 0, "Empty raid cargo was enumerated as a force.");
        cargoAccepted = false;
        try
        {
            AssertReport(actions.RaidAcceptance(valid, cargo), "ClashOfRim.Raid.TransportPodRejectNoColonist");
            AssertReport(actions.GiftAcceptance(valid, cargo, false), "cargo");
        }
        finally { cargoAccepted = true; }
    }

    private static void SerializationAndDispatch(ClientGiftModuleTests.Fixture f)
    {
        f.Capabilities = new(true, true);
        var view = new View();
        foreach (TransportersArrivalAction initial in new TransportersArrivalAction[]
        { new RemoteGiftTransportersArrivalAction(view, false), new RemoteGiftTransportersArrivalAction(view, true), new RemoteRaidTransportersArrivalAction(view) })
        {
            string path = Path.GetTempFileName();
            try
            {
                TransportersArrivalAction? saved = initial;
                Scribe.saver.InitSaving(path, "root"); Scribe_Deep.Look(ref saved, "arrival"); Scribe.saver.FinalizeSaving();
                var xml = XDocument.Load(path); var root = xml.Root!.Element("arrival")!;
                string key = root.Element("transporterKey")!.Value;
                Check(key.Length != 0 && root.Element("targetUserId")!.Value == "owner" && root.Element("targetLabel")!.Value == view.Label,
                    "Arrival save lost target identity, label or idempotency key.");
                saved = Load(path); Check(saved.GetType() == initial.GetType(), "Arrival type changed after native Scribe load.");
                sentGifts = startedRaids = 0; saved.Arrived(cargo, view.Tile);
                Check(sentKey == key && (saved is RemoteGiftTransportersArrivalAction ? sentGifts == 1 && startedRaids == 0 : startedRaids == 1 && sentGifts == 0),
                    "Arrival did not enter exactly its existing module with the saved key.");
                if (saved is RemoteGiftTransportersArrivalAction)
                    Check((string)sentGift![1] == "owner" && (string)sentGift[2] == "colony" && (string)sentGift[3] == "snapshot"
                        && (string)sentGift[4] == "map" && (string)sentGift[5] == "world" && (PlanetTile)sentGift[6] == view.Tile
                        && (string)sentGift[7] == view.Label && ReferenceEquals(sentGift[8], cargo)
                        && (bool)sentGift[9] == (root.Element("forcedDelivery")?.Value == "True"), "Gift module lost persisted dispatch arguments.");
                else
                    Check(raidTarget!.OwnerUserId == "owner" && raidTarget.OwnerColonyId == "colony" && raidTarget.SnapshotId == "snapshot"
                        && raidTarget.MapId == "map" && raidTarget.WorldObjectId == "world" && raidTarget.Tile == 7 && raidTarget.TileLayerId == 3
                        && raidTarget.Label == view.Label && raidTarget.CanRaid && raidForce!.UniqueLoadId == "transporters:" + key
                        && raidForce.Tile == 7 && raidForce.TileLayerId == 3, "Raid module lost target or force identity/layer.");
                root.Element("transporterKey")!.Remove(); xml.Save(path);
                saved = Load(path); saved.Arrived(cargo, view.Tile); string generated = sentKey!;
                saved.Arrived(cargo, view.Tile); Check(generated.Length != 0 && sentKey == generated, "Legacy arrival regenerated its idempotency key per invocation.");
                baseAccepted = false; giftCargoCalls = raidCargoCalls = 0;
                try { AssertReport(saved.StillValid(cargo, view.Tile), "base"); Check(giftCargoCalls + raidCargoCalls == 0, "Base refusal reached payload validators."); }
                finally { baseAccepted = true; }
                f.Capabilities = new(false, false); sentGifts = startedRaids = 0;
                saved.Arrived(cargo, view.Tile); Check(sentGifts + startedRaids == 0, "Configuration changes after loading did not block arrival.");
                f.Capabilities = new(true, true);
                root.Element("targetSnapshotId")!.Remove(); xml.Save(path);
                Check(Load(path).StillValid(cargo, view.Tile).Accepted, "Legacy arrival incorrectly required a source snapshot.");
                if (saved is RemoteRaidTransportersArrivalAction)
                {
                    root.Element("canRaid")!.Remove(); xml.Save(path);
                    AssertReport(Load(path).StillValid(cargo, view.Tile), "ClashOfRim.Raid.StatusTargetIncomplete");
                }
                root.Elements().Remove(); xml.Save(path);
                saved = Load(path);
                AssertReport(saved.StillValid(cargo, view.Tile), saved is RemoteRaidTransportersArrivalAction
                    ? "ClashOfRim.Raid.StatusTargetIncomplete" : "ClashOfRim.GiftDelivery.StatusTargetIncomplete");
            }
            finally { Scribe.ForceStop(); File.Delete(path); }
        }
        var runtime = new RemoteRuntimeWorldObject { OwnerUserId = "owner", OwnerColonyId = "colony", SourceMapId = "map", SourceLabel = "标题" };
        var colony = new RemoteColonyMapParent { OwnerUserId = "owner", OwnerColonyId = "colony", SourceMapId = "map", SourceLabel = "标题" };
        foreach (var action in new[] { new RemoteGiftTransportersArrivalAction(runtime, false), new RemoteGiftTransportersArrivalAction(colony, false) })
            Check(action.StillValid(cargo, view.Tile).Accepted, "Public gift constructor lost target fields.");
        var attached = ClientTransporterActions.Current;
        ClientTransporterActions.Attach(null);
        try
        {
            AssertReport(new RemoteGiftTransportersArrivalAction(view, false).StillValid(cargo, view.Tile), "ClashOfRim.GiftDelivery.Disabled");
            AssertReport(new RemoteRaidTransportersArrivalAction(view).StillValid(cargo, view.Tile), "ClashOfRim.Raid.PvpDisabled");
        }
        finally { ClientTransporterActions.Attach(attached); }
    }

    private static void ArrivalGuard(ClientGiftModuleTests.Fixture f)
    {
        bool Guard(TransportersArrivalAction action) => (bool)AccessTools.Method(typeof(RemoteGiftArrivalGuard), "Prefix").Invoke(null, new object[] { action })!;
        var gift = new RemoteGiftTransportersArrivalAction();
        Check(Guard(gift) && Guard(new RemoteRaidTransportersArrivalAction()), "Idle arrival was blocked.");
        Check(f.Sync.TryAcquireSnapshot(out var snapshot), "Could not acquire fixture snapshot lease.");
        using (snapshot) Check(!Guard(gift), "Gift crossed an active snapshot lease.");
        Check(f.Sync.TryBeginManual(out var manual), "Could not acquire fixture manual lease.");
        using (manual) Check(!Guard(gift), "Gift crossed an active manual operation.");
        try { f.Mutations.Begin(LocalMutationKind.GiftSend, "pending"); Check(!Guard(gift), "Gift crossed an unresolved mutation."); }
        finally { f.Mutations.Reset(); }
        Check(Guard(gift), "Releasing admission did not allow the retained cargo to arrive.");
    }

    private static TransportersArrivalAction Load(string path)
    { Scribe.loader.InitLoading(path); TransportersArrivalAction? value = null; Scribe_Deep.Look(ref value, "arrival"); Scribe.loader.FinalizeLoading(); return value!; }
    private static T Menu<T>(string name, params object[] args) => (T)AccessTools.Method(typeof(RemoteWorldObjectUiUtility), name).Invoke(null, args)!;
    private static void AssertReport(FloatMenuAcceptanceReport report, string? reason)
        => Check(report.Accepted == (reason is null) && (reason is null || report.FailReason == reason), "Unexpected transport admission: " + report.FailReason + " expected=" + reason);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static bool BaseValid(ref FloatMenuAcceptanceReport __result) { __result = baseAccepted ? FloatMenuAcceptanceReport.WasAccepted : FloatMenuAcceptanceReport.WithFailReason("base"); return false; }
    private static bool GiftCargo(string __1, ref FloatMenuAcceptanceReport __result)
    { giftCargoCalls++; surface = __1; __result = cargoAccepted ? FloatMenuAcceptanceReport.WasAccepted : FloatMenuAcceptanceReport.WithFailReason("cargo"); return false; }
    private static bool RaidCargo(ref bool __result) { raidCargoCalls++; __result = cargoAccepted; return false; }
    private static bool SendGift(object[] __args) { sentGifts++; sentGift = __args; sentKey = (string)__args[0]; return false; }
    private static bool StartRaid(IClientRaidCommandSource source, ModWorldMapMarkerDto target, ref Task __result)
    {
        startedRaids++; raidTarget = target; raidForce = (IRaidAttackForceSource)AccessTools.Field(typeof(ClientRaidCommandSource), "source").GetValue(source);
        sentKey = raidForce.UniqueLoadId.Substring("transporters:".Length); __result = Task.CompletedTask; return false;
    }
    private static bool Text(string key, ref string __result) { __result = key; return false; }
    private static bool Layer(ref int __result) { __result = 3; return false; }
    private static bool World(ref World? __result) { __result = null; return false; }
    private static bool Inactive(ref bool __result) { __result = false; return false; }
    private static bool Assemblies(ref IEnumerable<Assembly> __result) { __result = new[] { typeof(Pawn).Assembly, typeof(RemoteGiftTransportersArrivalAction).Assembly }; return false; }
    private static bool Quiet() => false;
    private sealed class View : IRemoteWorldObjectView
    {
        public string MarkerId => "marker"; public string OwnerUserId { get; init; } = "owner"; public string OwnerColonyId { get; init; } = "colony";
        public string OwnerFactionName => "faction"; public string RuntimeKind { get; init; } = "TradeableColony";
        public string SourceWorldObjectId { get; init; } = "world"; public string SourceLabel => "source-label";
        public string SourceMapId { get; init; } = "map"; public string SourceSnapshotId { get; init; } = "snapshot";
        public string RelatedEventId => "event"; public string RelationKind => "Hostile"; public bool OwnerOnline => false;
        public string OwnerLastSeenAtUtc => ""; public bool CanTrade => false; public bool CanRaid { get; init; } = true; public bool CanReinforce => false;
        public string RaidUnavailableReason => ""; public string RaidUnavailableUntilUtc => "";
        public PlanetTile Tile => new(7); public string Label => "目标 İ"; public WorldObject WorldObject => null!;
    }
}
