using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static class ClientColonyGizmoTests
{
    private static readonly List<WorldObject> objects = new();
    private static Action? iconHook;
    private static ColonyAppearanceSelection? iconSelection;
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Appearance(); Abandon().GetAwaiter().GetResult();
            }
            EngineCommands();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static void Appearance()
    {
        foreach (string change in new[] { "server", "user", "colony", "world-id", "auth", "login", "generation", "game", "world", "target", "unavailable" })
        {
            var f = new ClientColonyAppearanceTests.Fixture();
            var command = f.Flow.Command(f.CreateTarget()) ?? throw new Exception("Appearance command missing.");
            f.Change(change); command.Open();
            Check(f.Windows == 0 && f.Writes == 0 && f.Messages == 0, "Old appearance gizmo crossed " + change);
        }
        foreach (bool beforeClick in new[] { false, true })
        {
            var f = new ClientColonyAppearanceTests.Fixture();
            var command = beforeClick ? f.Flow.Command(f.CreateTarget()) : null;
            f.Once("read", () => f.Change("user"));
            if (beforeClick) command!.Open();
            else Check(f.Flow.Command(f.CreateTarget()) is null, "Reentrant read published a replacement account's gizmo.");
            Check(f.Windows == 0, "Appearance read crossed account ownership before presentation.");
        }
        var live = new ClientColonyAppearanceTests.Fixture();
        var old = new ColonyAppearanceSelection("Color", "old-icon", null, "#123456");
        var latest = new ColonyAppearanceSelection("Color", "new-icon", null, "#abcdef");
        live.Flow.SaveCurrent(old);
        var choice = live.Flow.Command(live.CreateTarget())!;
        live.Flow.SaveCurrent(latest); live.Ports.Snapshot = "advanced";
        choice.Open();
        Check(choice.Appearance == old && live.OpenedSelection == latest, "Gizmo icon mutated or editor opened an obsolete preference.");
        live.Save!(old); live.Save!(latest);
        Check(live.Registered.Count == 3 && live.Flow.Read() == old, "Gizmo bypassed existing one-use save pipeline.");
        choice.Open(); Check(live.Windows == 2, "Closing an editor permanently consumed its gizmo.");
        var unconfigured = new ClientColonyAppearanceTests.Fixture(); unconfigured.Ports.Server = "";
        Check(unconfigured.Flow.Command(unconfigured.CreateTarget()) is null, "Unconfigured appearance gizmo appeared.");
    }
    private static async Task Abandon()
    {
        foreach (string change in new[] { "server", "user", "colony", "world-id", "auth", "login", "generation", "game", "world", "target" })
        {
            var f = new ClientColonyAbandonmentTests.Fixture();
            var command = f.Module.Command(f.CreateTarget()) ?? throw new Exception("Abandon command missing.");
            f.Change(change); command();
            Check(f.Windows == 0 && f.Calls == 0 && f.Messages.Count == 0, "Old abandonment gizmo crossed " + change);
        }
        var live = new ClientColonyAbandonmentTests.Fixture();
        var choice = live.Module.Command(live.CreateTarget())!;
        live.Change("snapshot"); choice();
        await live.Run(live.ConfirmAction!()); await live.ConfirmAction!(); choice();
        Check(live.Calls == 1 && live.Menus == 1 && live.Windows == 1 && live.Context!.CurrentSnapshotId == "advanced",
            "Gizmo bypassed original confirmation, latest snapshot or terminal exit.");

        var busy = new ClientColonyAbandonmentTests.Fixture { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously), Fault = "reject" };
        var retry = busy.Module.Command(busy.CreateTarget())!; retry();
        var work = busy.ConfirmAction!(); await busy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        retry(); Check(busy.Windows == 1 && busy.Messages.Single().Text.Contains("StatusSubmitting"), "Busy gizmo opened another confirmation.");
        busy.Hold.SetResult(); await busy.Run(work); retry();
        Check(busy.Windows == 2 && busy.Calls == 1, "Busy refusal permanently consumed the gizmo.");
        busy.Fault = null; await busy.Run(busy.ConfirmAction!()); Check(busy.Menus == 1 && busy.Calls == 2, "Gizmo retry lost original execution path.");

        var unauthenticated = new ClientColonyAbandonmentTests.Fixture(); unauthenticated.Ports.Auth = "";
        unauthenticated.Module.Command(unauthenticated.CreateTarget())!();
        Check(unauthenticated.Windows == 0 && unauthenticated.Messages.Single().Text.Contains("StatusNotConfigured"), "Unauthenticated replacement lost its rejection.");
        var unconfigured = new ClientColonyAbandonmentTests.Fixture(); unconfigured.Ports.Colony = "";
        Check(unconfigured.Module.Command(unconfigured.CreateTarget()) is null, "Unconfigured abandonment replaced vanilla.");
    }
    private static void EngineCommands()
    {
        var originalGame = Current.Game; var creating = Current.CreatingWorld; var binding = ClientColonyGizmos.Current;
        var harmony = new Harmony("ClashOfRim.Tests.ColonyGizmos");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientColonyGizmoTests), prefix));
        Patch(AccessTools.PropertyGetter(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.AllWorldObjects)), nameof(Objects));
        Patch(AccessTools.PropertyGetter(typeof(MapParent), nameof(MapParent.HasMap)), nameof(NoMap));
        Patch(AccessTools.Method(typeof(RemoteColonyWorldIconCache), nameof(RemoteColonyWorldIconCache.GetTexture)), nameof(Icon));
        try
        {
            Current.CreatingWorld = null; Current.Game = New<Game>(); var world = New<World>(); Current.Game.World = world;
            var player = New<Faction>(); player.def = new FactionDef { isPlayer = true };
            world.factionManager = New<FactionManager>(); Set(world.factionManager, "ofPlayer", player); world.worldObjects = New<WorldObjectsHolder>();
            var colony = New<Settlement>(); colony.Name = "殖民地 Î"; colony.def = new WorldObjectDef { defName = "Settlement" }; Set(colony, "factionInt", player);
            objects.Add(colony);
            var a = new ClientColonyAppearanceTests.Fixture(); var b = new ClientColonyAbandonmentTests.Fixture();
            var selection = new ColonyAppearanceSelection("Color", "雪", null, "#123456"); a.Flow.SaveCurrent(selection);
            ClientColonyGizmos.Attach(null);
            var vanilla = new Command_Action { defaultLabel = "Original label", Order = 23f };
            Command abandon = vanilla; IEnumerable<Gizmo> gizmos = new[] { vanilla };
            PlayerColonyAbandonCommandPatch.Postfix(colony, ref abandon); PlayerColonyAppearanceGizmoPatch.Postfix(colony, ref gizmos);
            Check(ReferenceEquals(abandon, vanilla) && gizmos.SequenceEqual(new[] { vanilla }), "Unbound adapter changed vanilla gizmos.");
            ClientColonyGizmos.Attach(new(a.Flow, b.Module));
            PlayerColonyAbandonCommandPatch.Postfix(colony, ref abandon); PlayerColonyAppearanceGizmoPatch.Postfix(colony, ref gizmos);
            var appearance = (Command_Action)gizmos.Last();
            Check(gizmos.Count() == 2 && ReferenceEquals(gizmos.First(), vanilla) && appearance.Order == 980f && iconSelection == selection,
                "Appearance adapter lost original gizmos, ordering or captured icon.");
            Check(!ReferenceEquals(abandon, vanilla) && abandon.Label == vanilla.Label && abandon.icon == vanilla.icon && abandon.Order == vanilla.Order,
                "Abandon adapter lost vanilla presentation.");
            appearance.action(); ((Command_Action)abandon).action();
            Check(a.Windows == 1 && b.Windows == 1 && b.ConfirmationLabel == colony.LabelCap, "Patches failed to open existing modules.");
            objects.Clear(); appearance.action(); ((Command_Action)abandon).action();
            Check(a.Windows == 1 && b.Windows == 1, "Detached colony retained actionable gizmos."); objects.Add(colony);
            Current.Game.World = New<World>(); appearance.action(); ((Command_Action)abandon).action();
            Check(a.Windows == 1 && b.Windows == 1, "World replacement retained actionable gizmos."); Current.Game.World = world;
            ClientColonyGizmos.Attach(new(a.Flow, b.Module)); appearance.action(); ((Command_Action)abandon).action();
            Check(a.Windows == 1 && b.Windows == 1, "Replaced adapter retained old actions.");
            Set(colony, "factionInt", New<Faction>()); abandon = vanilla; gizmos = new[] { vanilla };
            PlayerColonyAbandonCommandPatch.Postfix(colony, ref abandon); PlayerColonyAppearanceGizmoPatch.Postfix(colony, ref gizmos);
            Check(ReferenceEquals(abandon, vanilla) && gizmos.Count() == 1, "Foreign colony received player actions."); Set(colony, "factionInt", player);
            iconHook = () => a.Change("user"); gizmos = new[] { vanilla };
            PlayerColonyAppearanceGizmoPatch.Postfix(colony, ref gizmos); ((Command_Action)gizmos.Last()).action();
            Check(a.Windows == 1, "Icon callback let an old gizmo open a new account.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = originalGame; Current.CreatingWorld = creating;
            ClientColonyGizmos.Attach(binding); objects.Clear(); iconHook = null; iconSelection = null;
        }
    }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
    private static bool Objects(ref List<WorldObject> __result) { __result = objects; return false; }
    private static bool NoMap(ref bool __result) { __result = false; return false; }
    private static bool Icon(string? mode, string? iconDefName, string? colorDefName, string? colorHex, ref Texture2D __result)
    { iconSelection = new(mode, iconDefName, colorDefName, colorHex); var hook = iconHook; iconHook = null; hook?.Invoke(); __result = null!; return false; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
