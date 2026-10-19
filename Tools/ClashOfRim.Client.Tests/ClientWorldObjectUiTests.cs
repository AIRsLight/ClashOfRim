using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static class ClientWorldObjectUiTests
{
    private static ClientWorldObjectCommandsTests.Fixture f = null!;
    private static ClientGiftCapabilities capabilities = new(true, true);
    private static readonly WindowStack stack = (WindowStack)RuntimeHelpers.GetUninitializedObject(typeof(WindowStack));
    private static readonly Dialog_MessageBox dialog = (Dialog_MessageBox)RuntimeHelpers.GetUninitializedObject(typeof(Dialog_MessageBox));
    private static World world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
    private static readonly WorldObjectsHolder objects = new();
    private static Action? confirm, cargoHook, textHook;
    private static bool nonGiftPawn;
    private static int launches, windows, closed;
    private static TransportersArrivalAction? arrival;
    private static Map? map;
    internal static void Run()
    {
        var previousCommands = (ClientWorldObjectCommands?)AccessTools.Field(typeof(RemoteWorldObjectUiUtility), "commands").GetValue(null);
        var previousTransporters = ClientTransporterActions.Current; var culture = CultureInfo.CurrentCulture;
        var harmony = new Harmony("ClashOfRim.Tests.WorldObjectUi");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientWorldObjectUiTests), prefix));
        var read = AccessTools.Method(typeof(ClientWorldObjectCommandTarget), nameof(ClientWorldObjectCommandTarget.Read));
        Patch(read, nameof(Read));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.World)), nameof(World));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WorldObjects)), nameof(Objects));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WindowStack)), nameof(Stack));
        Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), nameof(Add));
        Patch(AccessTools.Method(typeof(FloatMenuOption), "SetSizeMode"), nameof(SkipLayout));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Dialog_MessageBox)).Where(m => m.Name == nameof(Dialog_MessageBox.CreateConfirmation))) Patch(method, nameof(Confirm));
        Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get)), nameof(Texture));
        Patch(AccessTools.Method(typeof(RemoteWorldObjectUiUtility), nameof(RemoteWorldObjectUiUtility.ReadTileLayerId)), nameof(Layer));
        Patch(AccessTools.Method(typeof(GiftTransporterPayloadUtility), nameof(GiftTransporterPayloadUtility.CanSend)), nameof(GiftCargo));
        Patch(AccessTools.Method(typeof(GiftTransporterPayloadUtility), nameof(GiftTransporterPayloadUtility.ContainsNonGiftPawn)), nameof(NonGift));
        Patch(AccessTools.Method(typeof(TransportersArrivalActionUtility), nameof(TransportersArrivalActionUtility.AnyNonDownedColonist)), nameof(RaidCargo));
        Patch(AccessTools.PropertyGetter(typeof(MapParent), nameof(MapParent.HasMap)), nameof(HasMap));
        Patch(AccessTools.PropertyGetter(typeof(MapParent), nameof(MapParent.Map)), nameof(Map));
        Patch(AccessTools.Method(typeof(RemoteSessionMapUtility), nameof(RemoteSessionMapUtility.CloseObservationMap)), nameof(Close));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClashOfRimText)).Where(m => m.Name == nameof(ClashOfRimText.Key))) Patch(method, nameof(Text));
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Menus(); Gizmos();
            }
            harmony.Unpatch(read, HarmonyPatchType.Prefix, harmony.Id);
            TargetBinding();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        finally
        {
            harmony.UnpatchAll(harmony.Id); RemoteWorldObjectUiUtility.AttachCommands(previousCommands);
            ClientTransporterActions.Attach(previousTransporters); CultureInfo.CurrentCulture = culture;
            objects.AllWorldObjects.Clear(); confirm = cargoHook = textHook = null; map = null;
        }
    }

    private static void Setup()
    {
        f = new(); capabilities = new(true, true); confirm = cargoHook = textHook = null;
        launches = windows = closed = 0; arrival = null; map = null; nonGiftPawn = false;
        RemoteWorldObjectUiUtility.AttachCommands(f.Commands);
        // Only pre-launch validation is used here. Actual arrival-to-module
        // dispatch and admission are covered by RemoteTransporterActionsTests.
        ClientTransporterActions.Attach(new(null!, null!, () => capabilities));
    }
    private static FloatMenuOption[] Options()
        => RemoteWorldObjectUiUtility.BuildTransportersFloatMenuOptions(new View(), new[] { new ActiveTransporterInfo() },
            (tile, action) => { Check(tile == new PlanetTile(7), "Launch destination was redirected."); launches++; arrival = action; }).ToArray();
    private static FloatMenuOption Option(FloatMenuOption[] options, string kind)
        => options.Single(o => o.Label.Contains(kind, StringComparison.Ordinal));
    private static void Menus()
    {
        foreach (string kind in new[] { "TransportPodOption", "TransportPodGiftOption", "TransportPodForcedOption" })
        {
            Setup(); var options = Options(); Check(options.Length == 3, "World object lost a transport choice.");
            var option = Option(options, kind); option.action();
            if (kind != "TransportPodGiftOption")
            {
                Check(launches == 0 && windows == 1 && confirm is not null, "Raid/forced choice skipped confirmation.");
                option.action(); Check(windows == 1, "Repeated menu click opened duplicate confirmations.");
                f.Core.Target.Label = "changed-label"; f.Core.Target.SnapshotId = "changed-snapshot";
                confirm!(); confirm();
            }
            else option.action();
            Check(launches == 1 && arrival is not null, "Repeated menu/confirmation launched twice.");
            Check((string?)AccessTools.Field(arrival!.GetType(), "targetLabel").GetValue(arrival) == "original"
                && (string?)AccessTools.Field(arrival.GetType(), "targetSnapshotId").GetValue(arrival) == "",
                "Confirmed arrival reread mutable view labels or snapshot.");
            foreach (var other in options) other.action();
            Check(launches == 1, "Another choice from the consumed menu launched again.");
        }
        foreach (string change in new[] { "user", "auth", "game", "login", "unavailable", "removed", "owner", "tile", "layer", "raid", "reset" })
        {
            Setup(); Option(Options(), "TransportPodForcedOption").action();
            Check(confirm is not null, "No delayed confirmation captured.");
            f.Change(change); confirm!(); Check(launches == 0, "Confirmation crossed " + change);
        }
        foreach (string stage in new[] { "before-confirm", "during-cargo" })
        {
            Setup(); Option(Options(), "TransportPodForcedOption").action();
            if (stage == "before-confirm") capabilities = new(true, false);
            else cargoHook = () => capabilities = new(false, true);
            confirm!(); Check(launches == 0, "Confirmation bypassed configuration change " + stage);
        }
        Setup(); Option(Options(), "TransportPodForcedOption").action();
        cargoHook = () => f.Change("user"); confirm!(); Check(launches == 0, "Cargo compatibility callback crossed an account boundary.");
        Setup(); var stale = Option(Options(), "TransportPodForcedOption"); textHook = () => f.Change("game");
        stale.action(); Check(windows == 0 && launches == 0, "Confirmation formatting published into another Game.");
        Setup(); Option(Options(), "TransportPodForcedOption").action();
        ClientTransporterActions.Attach(new(null!, null!, () => capabilities));
        confirm!(); Check(launches == 0, "Old menu switched to a replacement transporter adapter.");
        Setup(); nonGiftPawn = true; f.Core.Target.RelationKind = "Neutral";
        Check(Options().Any(o => o.Label.Contains("OrbitalNonHostileDisabled", StringComparison.Ordinal))
            && Options().All(o => !o.Label.Contains("GiftDelivery", StringComparison.Ordinal)), "Orbital pawn restriction changed.");
    }

    private static void Gizmos()
    {
        Setup(); f.Blocked = true;
        var scout = RemoteWorldObjectUiUtility.BuildGizmos(new View(), Array.Empty<Gizmo>(), false).OfType<Command_Action>().Single();
        Check(AccessTools.Field(typeof(Gizmo), "disabled").GetValue(scout) is true, "Active remote map did not disable observation gizmo.");
        ClientObservationMapsTests.Game(scout.action); Check(f.Rejected == 1 && f.Core.DownloadCalls == 0, "Disabled observation callback bypassed active-map guard.");
        Setup(); var old = RemoteWorldObjectUiUtility.BuildGizmos(new View(), Array.Empty<Gizmo>(), false).OfType<Command_Action>().Single();
        f.Change("user"); ClientObservationMapsTests.Game(old.action);
        Check(f.Core.DownloadCalls == 0 && f.Core.Notices == 0, "Observation gizmo captured ownership only when clicked.");
        Setup(); map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); var original = map;
        var close = RemoteWorldObjectUiUtility.BuildGizmos(new View { Object = new RemoteColonyMapParent() }, Array.Empty<Gizmo>(), false)
            .OfType<Command_Action>().Single(c => c.defaultLabel.EndsWith(".Close", StringComparison.Ordinal));
        map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); close.action(); Check(closed == 0, "Old close gizmo closed a replacement map.");
        map = original; close.action(); close.action(); Check(closed == 1, "Close gizmo lost original-map binding or ran twice.");
    }

    private static void TargetBinding()
    {
        var target = new RemoteRuntimeWorldObject { MarkerId = "marker", RuntimeKind = "TradeableColony", OwnerUserId = "owner", OwnerColonyId = "colony", RelationKind = "Hostile" };
        objects.AllWorldObjects.Add(target); var bound = new ClientWorldObjectCommandTarget(target);
        Check(bound.Read() is { Allied: false }, "Registered world object was unavailable.");
        target.RelationKind = "Ally"; Check(bound.Read() is { Allied: true }, "Authoritative ally marker was ignored.");
        target.RelationKind = "Hostile"; target.CanReinforce = true; Check(bound.Read() is { Allied: true }, "Reinforcement ally permission was ignored.");
        objects.AllWorldObjects.Remove(target); Check(bound.Read() is null, "Removed world object was still actionable.");
        objects.AllWorldObjects.Add(target); world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
        Check(bound.Read() is null, "Old object crossed a world replacement within the same Game.");
    }

    private static bool Read(ref ClientWorldObjectTargetState? __result) { __result = f.Read(); return false; }
    private static bool World(ref World __result) { __result = world; return false; }
    private static bool Objects(ref WorldObjectsHolder __result) { __result = objects; return false; }
    private static bool Stack(ref WindowStack __result) { __result = stack; return false; }
    private static bool Add(Window __0) { Check(ReferenceEquals(__0, dialog), "Unexpected confirmation window."); windows++; return false; }
    private static bool Confirm(object[] __args, ref Dialog_MessageBox __result) { confirm = (Action)__args[1]; __result = dialog; return false; }
    private static bool Texture(ref Texture2D? __result) { __result = null; return false; }
    private static bool Layer(ref int __result) { __result = 2; return false; }
    private static bool GiftCargo(ref FloatMenuAcceptanceReport __result) { cargoHook?.Invoke(); __result = FloatMenuAcceptanceReport.WasAccepted; return false; }
    private static bool RaidCargo(ref bool __result) { cargoHook?.Invoke(); __result = true; return false; }
    private static bool NonGift(ref bool __result) { __result = nonGiftPawn; return false; }
    private static bool HasMap(ref bool __result) { __result = map is not null; return false; }
    private static bool Map(ref Map? __result) { __result = map; return false; }
    private static bool Close() { closed++; return false; }
    private static bool SkipLayout() => false;
    private static bool Text(string key, ref string __result) { textHook?.Invoke(); __result = key; return false; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class View : IRemoteWorldObjectView
    {
        internal WorldObject Object = new RemoteRuntimeWorldObject();
        private ModWorldMapMarkerDto Target => f.Core.Target;
        public string MarkerId => Target.MarkerId; public string OwnerUserId => Target.OwnerUserId; public string OwnerColonyId => Target.OwnerColonyId!;
        public string OwnerFactionName => "faction"; public string RuntimeKind => Target.Kind;
        public string SourceWorldObjectId => Target.WorldObjectId; public string SourceLabel => Target.Label!;
        public string SourceMapId => Target.MapId!; public string SourceSnapshotId => Target.SnapshotId ?? "";
        public string RelatedEventId => Target.RelatedEventId!; public string RelationKind => Target.RelationKind!; public bool OwnerOnline => false;
        public string OwnerLastSeenAtUtc => ""; public bool CanTrade => Target.CanTrade; public bool CanRaid => Target.CanRaid; public bool CanReinforce => Target.CanReinforce;
        public string RaidUnavailableReason => ""; public string RaidUnavailableUntilUtc => "";
        public PlanetTile Tile => new(Target.Tile); public string Label => Target.Label!; public WorldObject WorldObject => Object;
    }
}
