using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class ClientColonyAppearanceRuntimeTests
{
    private static readonly List<WorldObject> objects = new();
    private static int persisted, closed;
    internal static void Run()
    {
        var game = Current.Game; var creating = Current.CreatingWorld;
        var harmony = new Harmony("ClashOfRim.Tests.ColonyAppearanceRuntime");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.AllWorldObjects)),
                prefix: new HarmonyMethod(typeof(ClientColonyAppearanceRuntimeTests), nameof(Objects)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MapParent), nameof(MapParent.HasMap)),
                prefix: new HarmonyMethod(typeof(ClientColonyAppearanceRuntimeTests), nameof(NoMap)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(ModsConfig), nameof(ModsConfig.IdeologyActive)),
                prefix: new HarmonyMethod(typeof(ClientColonyAppearanceRuntimeTests), nameof(NoMap)));
            harmony.Patch(AccessTools.Method(typeof(ModSettings), nameof(ModSettings.Write)),
                prefix: new HarmonyMethod(typeof(ClientColonyAppearanceRuntimeTests), nameof(Persist)));
            harmony.Patch(AccessTools.Method(typeof(Window), nameof(Window.Close)),
                prefix: new HarmonyMethod(typeof(ClientColonyAppearanceRuntimeTests), nameof(Close)));
            Current.CreatingWorld = null; var live = New<Game>(); var world = New<World>(); live.World = world; Current.Game = live;
            var player = New<Faction>(); player.def = new FactionDef { isPlayer = true };
            world.factionManager = New<FactionManager>(); Set(world.factionManager, "ofPlayer", player); world.worldObjects = New<WorldObjectsHolder>();
            var colony = New<Settlement>(); colony.Name = "Colony"; colony.def = new WorldObjectDef { defName = "Settlement" }; Set(colony, "factionInt", player);
            objects.Add(colony); var target = new ClientColonyAppearanceTarget(colony);
            Check(target.IsAvailable && target.Label == colony.LabelCap, "Player settlement target or label lost.");
            colony.def.defName = "ClashOfRim_Proxy"; Check(!target.IsAvailable, "Proxy received an appearance editor.");
            colony.def.defName = "QuestSite"; Check(!target.IsAvailable, "Unloaded non-settlement received an editor.");
            colony.def.defName = "PlayerSettlement"; Check(target.IsAvailable, "Player settlement alias rejected.");
            objects.Clear(); Check(!target.IsAvailable, "Detached colony remained editable."); objects.Add(colony);
            Set(colony, "factionInt", New<Faction>()); Check(!target.IsAvailable, "Foreign colony remained editable."); Set(colony, "factionInt", player);
            Set(colony, "destroyed", true); Check(!target.IsAvailable, "Destroyed colony remained editable."); Set(colony, "destroyed", false);
            live.World = New<World>(); Check(!target.IsCurrent && !target.IsAvailable, "World replacement retained editor target.");
            live.World = world; Current.Game = New<Game>(); Current.Game.World = world; Check(!target.IsCurrent, "Replacement game retained editor target.");

            var settings = new ClashOfRimSettings { ServerBaseUrl = " WSS://Server/ ", CurrentWorldConfigurationId = "world", UserId = "user", ColonyId = "colony" };
            var store = new ClientColonyAppearanceStore(settings);
            Check(store.ReadAccount() == new ClientColonyAppearanceAccount(" WSS://Server/ ", "world", "user", "colony"), "Persistence adapter normalized away legacy raw identity.");
            store.Write("old-key", "packed"); settings.UserId = "other";
            Check(store.Read("old-key") == "packed", "Storage recomputed a captured key.");
            store.Write("old-key", null); Check(!settings.ColonyAppearancesByAccount.ContainsKey("old-key"), "Reset retained dictionary entry.");
            settings.ColonyAppearancesByAccount = null!; store.Write("recovered", "value"); persisted = 0; store.Persist();
            Check(store.Read("recovered") == "value" && persisted == 1, "Persistence adapter lost the existing settings write path.");
            ColonyAppearanceSelection? chosen = null;
            var selection = new ColonyAppearanceSelection("Color", null, null, "#123456");
            var dialog = new ColonyAppearanceDialogWindow("frozen colony", selection, value => chosen = value);
            Check((string?)AccessTools.Field(typeof(ColonyAppearanceDialogWindow), "selectedHex").GetValue(dialog) == "#123456"
                && (string?)AccessTools.Field(typeof(ColonyAppearanceDialogWindow), "colony").GetValue(dialog) == "frozen colony", "Window reread live settings or target.");
            closed = 0;
            AccessTools.Method(typeof(ColonyAppearanceDialogWindow), "Save").Invoke(dialog, null);
            Check(chosen == selection && closed == 1, "Window did not submit its selection through the supplied callback and close.");
        }
        finally { harmony.UnpatchAll(harmony.Id); Current.Game = game; Current.CreatingWorld = creating; objects.Clear(); }
    }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
    private static bool Objects(ref List<WorldObject> __result) { __result = objects; return false; }
    private static bool NoMap(ref bool __result) { __result = false; return false; }
    private static bool Persist() { persisted++; return false; }
    private static bool Close() { closed++; return false; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
