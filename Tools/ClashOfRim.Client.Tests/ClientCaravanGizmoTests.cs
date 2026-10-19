using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.Trades;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static class ClientCaravanGizmoTests
{
    private static readonly string[] Routes = { "gift", "trade", "support", "raid" };
    private static readonly Type[] Adapters = { typeof(GiftCaravanGizmoPatches), typeof(TradeCaravanGizmoPatches), typeof(SupportCaravanGizmoPatches), typeof(RaidCaravanGizmoPatches) };
    private static Runtime current = null!;
    internal static void Run()
    {
        using var native = new Runtime(); current = native;
        using var ui = new ClientOverlayTestRuntime(nameof(ClientCaravanGizmoTests));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (string route in Routes)
            foreach (int count in new[] { 0, 1, 2 })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language); native.Reset(count);
                var commands = Build(route);
                Check(commands.Length == (count == 0 ? 0 : route == "gift" ? 2 : 1), "Unexpected button count: " + route);
                foreach (var command in commands)
                {
                    Check(command.defaultLabel.EndsWith(count == 1 ? "Gizmo" : "GizmoCount", StringComparison.Ordinal), "Wrong singular/plural key.");
                    Check(command.defaultDesc.EndsWith("GizmoDesc", StringComparison.Ordinal) && command.icon is not null, "Missing command presentation.");
                    command.action(); command.action();
                }
                Check(native.Opened.Count == commands.Length, "Repeated button opened another menu.");
                Check(native.Opened.All(x => x.Count == count), "Lost target count.");
                if (route == "gift" && count > 0) Check(native.Opened.Select(x => x.Route).SequenceEqual(new[] { "gift", "forced" }), "Gift force mode was swapped.");
            }
            foreach (string route in Routes)
            foreach (string phase in new[] { "read", "label", "click" })
            foreach (string change in Changes)
            {
                native.Reset(1);
                if (phase == "read") native.OnRead = () => Change(change, route);
                if (phase == "label") ui.Translating = _ => { ui.Translating = null; Change(change, route); };
                var commands = Build(route);
                if (phase == "click") Change(change, route);
                foreach (var command in commands) command.action();
                bool allowed = change is "snapshot" or "password";
                Check(native.Opened.Count == (allowed ? route == "gift" ? 2 : 1 : 0), $"Stale command used new owner: {route}/{phase}/{change}");
                ui.Reset();
            }
            RefusalsAndCargo();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static readonly string[] Changes = { "user", "auth", "server", "colony", "login", "generation", "unavailable", "game", "world", "tile", "layer", "binding", "destroyed", "unspawned", "not-player", "snapshot", "password" };
    private static void Change(string change, string route)
    {
        var f = current;
        switch (change)
        {
            case "user": f.Ports.User = "other"; break;
            case "auth": f.Ports.Auth = "other"; break;
            case "server": f.Ports.Server += "/other"; break;
            case "colony": f.Ports.Colony = "other"; break;
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = f.Ports.User, AuthToken = f.Ports.Auth, SessionId = "next" }); break;
            case "generation": ClientSessionWork.Reset(); break;
            case "unavailable": f.Session.BeginUnavailable(null, null); break;
            case "game": Current.Game = New<Game>(); break;
            case "world": f.World = New<World>(); break;
            case "tile": f.Tile = new(99, 2); break;
            case "layer": f.Tile = new(7, 3); break;
            case "binding": AccessTools.Field(Adapters[Array.IndexOf(Routes, route)], "binding").SetValue(null, null); break;
            case "destroyed": f.Destroyed = true; break;
            case "unspawned": f.Spawned = false; break;
            case "not-player": f.Player = false; break;
            case "snapshot": f.Ports.Snapshot = "advanced"; break;
            case "password": f.Ports.Password = "new"; break;
        }
    }
    private static Command_Action[] Build(string route, bool missing = false)
    {
        Caravan caravan = missing ? null! : current.Caravan;
        var original = new Command_Action(); int enumerations = 0;
        IEnumerable<Gizmo> Original() { enumerations++; yield return original; }
        IEnumerable<Gizmo> result = Original();
        switch (route)
        {
            case "gift": GiftCaravanGizmoPatches.Postfix(caravan, ref result); break;
            case "trade": TradeCaravanGizmoPatches.Postfix(caravan, ref result); break;
            case "support": SupportCaravanGizmoPatches.Postfix(caravan, ref result); break;
            case "raid": RaidCaravanGizmoPatches.Postfix(caravan, ref result); break;
        }
        Check(enumerations == 0, "Adapter eagerly consumed the original gizmos.");
        var all = result.ToArray();
        Check(ReferenceEquals(all[0], original) && enumerations == 1, "Original gizmos were replaced or repeated.");
        return all.Skip(1).Cast<Command_Action>().ToArray();
    }
    private static void RefusalsAndCargo()
    {
        foreach (string route in Routes)
        {
            current.Reset(1); Check(Build(route, missing: true).Length == 0, "Missing caravan received a command.");
            Current.Game = null; Check(Build(route).Length == 0, "Missing game received a command.");
            current.Reset(1); current.World = null!; Check(Build(route).Length == 0, "Missing world received a command.");
        }
        foreach (string route in Routes)
        foreach (string change in new[] { "destroyed", "unspawned", "not-player", "binding" })
        { current.Reset(1); Change(change, route); Check(Build(route).Length == 0, "Ineligible source received command: " + change); }
        foreach (string route in new[] { "gift", "support", "raid" })
        {
            current.Reset(1); var commands = Build(route);
            current.Cargo.Clear(); current.Members.Clear();
            foreach (var command in commands) command.action();
            Check(current.Opened.Count == 0 && Build(route).Length == 0, "Empty source opened a menu: " + route);
        }
        current.Reset(1); var choices = Build("support");
        current.Members.Clear(); current.Members.Add(New<Pawn>()); choices.Single().action();
        Check(current.Opened.Count == 0, "Old support choice adopted a replacement pawn.");
        Check(Build("support").Length == 1, "A newly drawn command could not use the replacement pawn.");
        current.Reset(1); current.Owners = false;
        Check(Build("support").Length == 0 && Build("raid").Length == 0, "Guest pawn counted as an owner.");
        current.Reset(1); current.Dead = true;
        Check(Build("support").Length == 0 && Build("raid").Length == 0, "Dead pawn counted as an owner.");
        current.Reset(1); var gifts = Build("gift"); current.Cargo.Clear();
        gifts[0].action(); current.Cargo.Add(new Thing { def = new ThingDef { category = ThingCategory.Item } }); gifts[0].action();
        Check(current.Opened.Count == 1, "Unavailable cargo permanently consumed the button.");
        current.Reset(1); choices = Build("trade"); current.TargetCount = 4; choices[0].action();
        Check(current.Opened.Single().Count == 1, "Click adopted a newer target list.");
    }
    private sealed class Runtime : IDisposable
    {
        private readonly Harmony harmony = new(nameof(ClientCaravanGizmoTests));
        private readonly Game savedGame = Current.Game;
        private readonly object?[] bindings = Adapters.Select(t => AccessTools.Field(t, "binding").GetValue(null)).ToArray();
        internal readonly EventModuleTestPorts Ports = new();
        internal ClientSessionLifecycle Session = null!;
        internal World World = null!;
        internal Caravan Caravan = null!;
        internal PlanetTile Tile;
        internal bool Destroyed, Spawned, Player, Owners, Dead;
        internal int TargetCount;
        internal readonly List<Pawn> Members = new();
        internal readonly List<Thing> Cargo = new();
        internal readonly List<(string Route, int Count)> Opened = new();
        internal Action? OnRead;
        internal Runtime()
        {
            void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientCaravanGizmoTests), prefix));
            Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.World)), nameof(World));
            Patch(AccessTools.PropertyGetter(typeof(WorldObject), nameof(WorldObject.Tile)), nameof(ReadTile));
            Patch(AccessTools.PropertyGetter(typeof(WorldObject), nameof(WorldObject.Destroyed)), nameof(Destroyed));
            Patch(AccessTools.PropertyGetter(typeof(WorldObject), nameof(WorldObject.Spawned)), nameof(Spawned));
            Patch(AccessTools.PropertyGetter(typeof(Caravan), nameof(Caravan.IsPlayerControlled)), nameof(Player));
            Patch(AccessTools.PropertyGetter(typeof(Caravan), nameof(Caravan.PawnsListForReading)), nameof(Members));
            Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Dead)), nameof(Dead));
            Patch(AccessTools.Method(typeof(Caravan), nameof(Caravan.ContainsPawn)), nameof(Contains));
            Patch(AccessTools.Method(typeof(Caravan), nameof(Caravan.IsOwner)), nameof(Owner));
            Patch(AccessTools.Method(typeof(CaravanInventoryUtility), nameof(CaravanInventoryUtility.AllInventoryItems)), nameof(Items));
            Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get)), nameof(Texture));
            Patch(AccessTools.Method(typeof(ClientGiftActions), nameof(ClientGiftActions.FindTargets)), nameof(GiftTargets));
            Patch(AccessTools.Method(typeof(ClientGiftActions), nameof(ClientGiftActions.OpenMenu)), nameof(GiftMenu));
            Patch(AccessTools.Method(typeof(ClientTradeActions), nameof(ClientTradeActions.FindFulfillable)), nameof(TradeTargets));
            Patch(AccessTools.Method(typeof(ClientTradeActions), nameof(ClientTradeActions.OpenCaravanMenu)), nameof(TradeMenu));
            Patch(AccessTools.Method(typeof(ClientWorldMarkers), nameof(ClientWorldMarkers.FindTargets)), nameof(WorldTargets));
            Patch(AccessTools.Method(typeof(ClientSupportActions), nameof(ClientSupportActions.OpenMenu)), nameof(SupportMenu));
            Patch(AccessTools.Method(typeof(ClientRaidCommands), nameof(ClientRaidCommands.OpenCaravanMenu)), nameof(RaidMenu));
        }
        internal void Reset(int count)
        {
            ClientSessionWork.Reset(); Ports.User = "alice"; Ports.Auth = "auth"; Ports.Colony = "colony"; Ports.Server = "http://localhost"; Ports.Snapshot = "base";
            Ports.Password = null;
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            Current.Game = New<Game>(); World = New<World>(); Caravan = New<Caravan>(); Tile = new(7, 2);
            Destroyed = Dead = false; Spawned = Player = Owners = true; TargetCount = count; OnRead = null;
            Members.Clear(); Members.Add(New<Pawn>()); Cargo.Clear(); Cargo.Add(new Thing { def = new ThingDef { category = ThingCategory.Item } }); Opened.Clear();
            GiftCaravanGizmoPatches.Attach(Session, New<ClientGiftActions>());
            TradeCaravanGizmoPatches.Attach(Session, New<ClientTradeActions>());
            SupportCaravanGizmoPatches.Attach(Session, New<ClientSupportActions>(), New<ClientWorldMarkers>());
            RaidCaravanGizmoPatches.Attach(Session, New<ClientRaidCommands>(), New<ClientWorldMarkers>());
        }
        public void Dispose()
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = savedGame;
            for (int i = 0; i < Adapters.Length; i++) AccessTools.Field(Adapters[i], "binding").SetValue(null, bindings[i]);
        }
    }
    private static bool World(ref World __result) { __result = current.World; return false; }
    private static bool ReadTile(ref PlanetTile __result) { __result = current.Tile; return false; }
    private static bool Destroyed(ref bool __result) { __result = current.Destroyed; return false; }
    private static bool Spawned(ref bool __result) { __result = current.Spawned; return false; }
    private static bool Player(ref bool __result) { __result = current.Player; return false; }
    private static bool Dead(ref bool __result) { __result = current.Dead; return false; }
    private static bool Members(ref List<Pawn> __result) { __result = current.Members; return false; }
    private static bool Contains(Pawn __0, ref bool __result) { __result = current.Members.Any(p => ReferenceEquals(p, __0)); return false; }
    private static bool Owner(ref bool __result) { __result = current.Owners; return false; }
    private static bool Items(ref IEnumerable<Thing> __result) { __result = current.Cargo; return false; }
    private static bool Texture(ref Texture2D __result) { __result = New<Texture2D>(); return false; }
    private static List<ModWorldMapMarkerDto> Targets(string id)
    {
        var result = Enumerable.Range(0, current.TargetCount).Select(i => new ModWorldMapMarkerDto { MarkerId = id + i, Tile = 7, TileLayerId = 2 }).ToList();
        var callback = current.OnRead; current.OnRead = null; callback?.Invoke(); return result;
    }
    private static bool GiftTargets(Caravan __0, bool __1, ref IReadOnlyList<ModWorldMapMarkerDto> __result)
    { Check(ReferenceEquals(__0, current.Caravan), "Gift queried another caravan."); __result = Targets(__1 ? "forced" : "gift"); return false; }
    private static bool WorldTargets(int __0, int __1, ClientColonyTargetKind __2, ref IReadOnlyList<ModWorldMapMarkerDto> __result)
    { Check(__0 == 7 && __1 == 2, "Target query flattened or replaced the tile/layer."); __result = Targets(__2 == ClientColonyTargetKind.Raid ? "raid" : "support"); return false; }
    private static bool TradeTargets(Caravan __0, ref IReadOnlyList<ModTradeOrderSummaryDto> __result)
    { Check(ReferenceEquals(__0, current.Caravan), "Trade queried another caravan."); __result = Targets("trade").Select(x => new ModTradeOrderSummaryDto { EventId = x.MarkerId }).ToArray(); return false; }
    private static bool GiftMenu(Caravan __0, IReadOnlyList<ModWorldMapMarkerDto> __1, bool __2) => Menu(__0, __1.Select(x => x.MarkerId).ToArray(), __2 ? "forced" : "gift");
    private static bool TradeMenu(Caravan __0, IReadOnlyList<ModTradeOrderSummaryDto> __1) => Menu(__0, __1.Select(x => x.EventId).ToArray(), "trade");
    private static bool SupportMenu(Caravan __0, IReadOnlyList<ModWorldMapMarkerDto> __1, IReadOnlyList<Pawn> __2)
    { Check(__2.Count > 0 && __2.All(p => current.Members.Any(m => ReferenceEquals(m, p))), "Support borrowed nonmember pawns."); return Menu(__0, __1.Select(x => x.MarkerId).ToArray(), "support"); }
    private static bool RaidMenu(IClientRaidCommandSource __0, IReadOnlyList<ModWorldMapMarkerDto> __1)
    { Check(__0.IsCurrent && __0.IsAvailable && __0.PawnCount == 1, "Raid lost original source."); return Menu(current.Caravan, __1.Select(x => x.MarkerId).ToArray(), "raid"); }
    private static bool Menu(Caravan source, string[] ids, string route)
    { Check(ReferenceEquals(source, current.Caravan) && ids.All(x => x.StartsWith(route, StringComparison.Ordinal)), "Menu changed source, targets or route."); current.Opened.Add((route, ids.Length)); return false; }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
