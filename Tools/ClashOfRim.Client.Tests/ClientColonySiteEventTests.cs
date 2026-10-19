using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class ClientColonySiteEventTests
{
    private static Faction player = null!;
    private static bool home;
    private static Task? dispatched;
    private static readonly List<string> warnings = new();
    internal static void Run()
    {
        var binding = ClientColonySiteEvents.Current; var culture = CultureInfo.CurrentCulture;
        var harmony = new Harmony("ClashOfRim.Tests.ColonySiteEvents");
        void Patch(Type type, string method, string prefix) => harmony.Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(typeof(ClientColonySiteEventTests), prefix));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer)), prefix: new HarmonyMethod(typeof(ClientColonySiteEventTests), nameof(Player)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.IsPlayerHome)), prefix: new HarmonyMethod(typeof(ClientColonySiteEventTests), nameof(Home)));
        harmony.Patch(AccessTools.Method(typeof(ClientColonySiteEvents), nameof(ClientColonySiteEvents.NotifyAsync)), postfix: new HarmonyMethod(typeof(ClientColonySiteEventTests), nameof(Dispatched)));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClashOfRimText)).Where(m => m.Name == nameof(ClashOfRimText.Key)))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientColonySiteEventTests), nameof(Text)));
        Patch(typeof(Log), nameof(Log.Warning), nameof(Warning));
        player = New<Faction>(); player.def = new FactionDef { isPlayer = true };
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (var kind in Enum.GetValues<ClientColonySiteEvent>()) Events(kind).GetAwaiter().GetResult();
            }
            Polling(harmony).GetAwaiter().GetResult();
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); ClientColonySiteEvents.Attach(binding); CultureInfo.CurrentCulture = culture;
            warnings.Clear(); dispatched = null; ClientSessionWork.Reset();
        }
    }
    private static async Task Polling(Harmony harmony)
    {
        using var runtime = new ClientGameComponentTestRuntime(harmony.Id);
        harmony.Patch(AccessTools.Method(typeof(ClientColonySiteEvents), nameof(ClientColonySiteEvents.PollAsync)),
            postfix: new HarmonyMethod(typeof(ClientColonySiteEventTests), nameof(Dispatched)));
        var a = new ClientColonyAppearanceTests.Fixture(); var f = new ClientColonyRegistrationTests.Fixture();
        a.Flow.SaveCurrent(new("Color", null, null, "#123abc"));
        var events = new ClientColonySiteEvents(f.Registration, a.Flow); var game = New<Game>(); Current.Game = game; f.Ports.GameOwner = game;
        var component = new ClashOfRimGameComponent(game);
        async Task Tick(int ticks) { runtime.Ticks = ticks; dispatched = null; component.GameComponentTick(); await f.Ports.PumpUntil(dispatched ?? Task.CompletedTask); }
        await Tick(0); Check(dispatched is null, "Unbound component dispatched site registration.");
        ClientColonySiteEvents.Attach(events); await Tick(0); await Tick(9999);
        Check(f.Calls.Count == 1 && f.CapturedAppearance?.ColorHex == "#123abc", "Component polling bypassed module or lost appearance.");
        f.Sites[0].Label = "new label"; await Tick(10000); Check(f.Calls.Count == 2, "Component omitted due registration.");
        Current.Game = New<Game>(); f.Ports.GameOwner = Current.Game; await Tick(20000);
        Check(f.Calls.Count == 2, "Old component polled registration for the new game.");
        component = new ClashOfRimGameComponent(Current.Game); await Tick(20000);
        Check(f.Calls.Count == 3, "New game inherited an old registration deadline.");
        ClientColonySiteEvents.Attach(new(f.Registration, a.Flow));
        f.Sites[0].Label = "replacement";
        await f.Ports.PumpUntil(events.PollAsync(Current.Game, 30000));
        Check(f.Calls.Count == 3, "Detached event adapter polled registration.");
        await Tick(30000); Check(f.Calls.Count == 4, "Replacement event adapter did not reuse registration.");
        a.Once("read", () => f.Change("user")); await Tick(40000);
        Check(f.Calls.Count == 4, "Preference read registered old appearance for another account.");
        await Tick(40001); Check(f.Calls.Count == 5, "New account remained throttled by the old capture's deadline.");
    }
    private static async Task Events(ClientColonySiteEvent kind)
    {
        var a = new ClientColonyAppearanceTests.Fixture(); var f = new ClientColonyRegistrationTests.Fixture();
        a.Flow.SaveCurrent(new("Color", null, null, "#123abc"));
        var events = new ClientColonySiteEvents(f.Registration, a.Flow); ClientColonySiteEvents.Attach(events);
        var parent = New<Settlement>(); AccessTools.Field(typeof(WorldObject), "factionInt").SetValue(parent, player);
        var map = New<Map>(); home = false; warnings.Clear();
        int checks = 0; bool suppressed = false; Action? onCheck = null;
        var token = ClashOfRimCompatibilityApi.CreateRegistrationToken("colony-site-event-test");
        ClashOfRimCompatibilityApi.UseRegistrationToken(token, () => ClashOfRimCompatibilityApi.RegisterPlayerColonySiteRegistrationSuppressor(() =>
        { checks++; var hook = onCheck; onCheck = null; hook?.Invoke(); return suppressed; }));
        var reasons = new List<string>(); f.Registration.Diagnostic += (text, _) => reasons.Add(text);
        async Task Fire()
        {
            dispatched = null;
            if (kind == ClientColonySiteEvent.Settled) PlayerColonySiteSettledPatch.Postfix(parent, map);
            else PlayerColonySiteRemovedPatch.Postfix(parent, map);
            await f.Ports.PumpUntil(dispatched ?? Task.CompletedTask);
        }
        try
        {
            ClientColonySiteEvents.Attach(null); await Fire(); Check(dispatched is null, "Unbound site patch dispatched an event."); ClientColonySiteEvents.Attach(events);
            await Fire();
            string reason = kind == ClientColonySiteEvent.Settled ? "ReasonPlayerColonyMapCreated" : "ReasonPlayerColonyMapRemoved";
            Check(f.Calls.Count == 1 && checks == 1 && f.CapturedAppearance?.ColorHex == "#123abc" && reasons.Any(r => r.Contains(reason)),
                "Site patch lost event reason, player parent or appearance: " + kind);
            f.Registration.Suppressed = true; await Fire(); f.Registration.Suppressed = false;
            Check(checks == 1 && f.Calls.Count == 1, "Relocation suppression still ran compatibility or submitted.");
            suppressed = true; await Fire(); suppressed = false; await f.Run(ClientColonyRegistrationKind.Sites);
            Check(f.Calls.Count == 1 && checks == 2, "Compatibility-suppressed event invalidated accepted sites.");
            AccessTools.Field(typeof(WorldObject), "factionInt").SetValue(parent, New<Faction>());
            await Fire(); Check(f.Calls.Count == 1 && checks == 2, "Foreign context invoked compatibility or registration.");
            home = true; await Fire(); Check(f.Calls.Count == 2 && checks == 3, "Player-home map lost context when its parent was foreign."); home = false;
            AccessTools.Field(typeof(WorldObject), "factionInt").SetValue(parent, player);
            int queues = f.Ports.Queued; onCheck = () => f.Change("user"); await Fire();
            Check(f.Calls.Count == 2 && f.Ports.Queued == queues, "Compatibility callback registered an old site event for a replacement account.");
            onCheck = () => ClientColonySiteEvents.Attach(new(f.Registration, a.Flow)); await Fire();
            Check(f.Calls.Count == 2, "Replaced event adapter queued old work."); ClientColonySiteEvents.Attach(events);
            a.Once("read", () => throw new InvalidOperationException("appearance storage")); await Fire();
            Check(f.Calls.Count == 2 && warnings.Single().Contains("appearance storage"), "Capture failure escaped the engine patch or queued work.");
        }
        finally { ClashOfRimCompatibilityApi.RevokeRegistrationToken(token); }
    }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static bool Player(ref Faction __result) { __result = player; return false; }
    private static bool Home(ref bool __result) { __result = home; return false; }
    private static bool Text(string __0, ref string __result) { __result = __0; return false; }
    private static bool Warning(string __0) { warnings.Add(__0); return false; }
    private static void Dispatched(Task __result) => dispatched = __result;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
