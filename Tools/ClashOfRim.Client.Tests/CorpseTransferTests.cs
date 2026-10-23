using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Trades;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class CorpseTransferTests
{
    private static readonly List<Thing> delivered = new();
    private static readonly TickManager ticks = new();
    private static ThingDef race = null!;
    private static bool restoreFails;
    private static int restores, corpseCreations, rotCallbacks;

    internal static void Run()
    {
        using var definitions = new DefDatabaseTestScope<ThingDef>();
        var harmony = new Harmony("ClashOfRim.Tests.CorpseTransfer");
        var culture = CultureInfo.CurrentCulture;
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(CorpseTransferTests), prefix));
        // Serialization, pawn creation and game rendering are external boundaries;
        // item capture, both delivery routes and component metadata run unchanged.
        Patch(AccessTools.Method(typeof(ScribeSaver), nameof(ScribeSaver.DebugOutputFor)), nameof(Serialize));
        Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.LabelShort)), nameof(PawnLabel));
        Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.LabelCapNoCount)), nameof(ItemLabel));
        Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.MarketValue)), nameof(Value));
        Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.MaxHitPoints)), nameof(Maximum));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.TickManager)), nameof(TickManager));
        Patch(AccessTools.Method(typeof(GiftPawnPackageUtility), nameof(GiftPawnPackageUtility.TryRestoreCorpsePawn)), nameof(Restore));
        Patch(AccessTools.Method(typeof(GiftPawnPackageUtility), nameof(GiftPawnPackageUtility.TryRestoreTradePawn)), nameof(RejectLivePawnRestore));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Pawn)).Where(m => m.Name == nameof(Pawn.MakeCorpse))) Patch(method, nameof(MakeCorpse));
        Patch(AccessTools.Method(typeof(Corpse), nameof(Corpse.RotStageChanged)), nameof(RotStageChanged));
        Patch(AccessTools.Method(typeof(CaravanInventoryUtility), nameof(CaravanInventoryUtility.GiveThing)), nameof(GiveThing));
        Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }), nameof(Skip));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClashOfRimText)).Where(m => m.Name == nameof(ClashOfRimText.Key))) Patch(method, nameof(Translate));
        try
        {
            var corpseDef = new ThingDef { defName = "CorpseTransferTest", label = "corpse", category = ThingCategory.Item, thingClass = typeof(Corpse), stackLimit = 1, useHitPoints = true };
            var derivedDef = new ThingDef { defName = "DerivedCorpseTransferTest", label = "derived corpse", category = ThingCategory.Item, thingClass = typeof(DerivedCorpse), stackLimit = 1, useHitPoints = true };
            race = new ThingDef { defName = "CorpseTransferRace", label = "pawn", category = ThingCategory.Pawn, race = new RaceProperties { corpseDef = corpseDef } };
            definitions.Replace(new[] { corpseDef, derivedDef, race });
            using var fixture = new ClientTradeMarketTests.Fixture();
            var actions = new ClientTradeActions(null!, fixture.State, fixture.Session, null!, () => Array.Empty<ModWorldMapMarkerDto>(), () => null, () => null);
            var map = new Map { uniqueID = 17 };
            var window = new TradeOrderDialogWindow(actions, map);
            var caravan = (Caravan)RuntimeHelpers.GetUninitializedObject(typeof(Caravan));
            caravan.ID = 27;
            foreach (var def in new[] { corpseDef, derivedDef })
            foreach (string locale in new[] { "zh-CN", "fr-FR" })
            foreach (float progress in new[] { 0f, 180000.25f, 50000000f })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(locale);
                race.race.corpseDef = def;
                var source = NewCorpse(NewPawn(), progress);
                source.HitPoints = 51; source.Age = 543210; source.everBuriedInSarcophagus = true;
                AccessTools.Field(typeof(Corpse), "vanishAfterTimestamp").SetValue(source, 987654);
                foreach (string path in new[] { "offer", "map", "caravan", "pod" })
                {
                    var reference = path switch
                    {
                        "offer" => (ModThingReferenceDto)AccessTools.Method(typeof(TradeOrderDialogWindow), "ToOfferReference").Invoke(window, new object[] { new TradeOfferSelection(source, 1) })!,
                        "map" => TradeMapFulfillmentUtility.BuildThingReference(source, map, "alice", "colony", "snapshot", 1),
                        "caravan" => TradeCaravanFulfillmentUtility.BuildThingReference(source, caravan, "alice", "colony", "snapshot", 1),
                        _ => (ModThingReferenceDto)AccessTools.Method(typeof(GiftTransporterPayloadUtility), "ToThingReference").Invoke(null, new object[] { source, "alice", "colony", "snapshot", "pod", false })!
                    };
                    Check(reference.PawnPackage?.Status?.Dead == true && reference.PawnPackage.Scribe?.Xml == "<pawn>dead-pawn-state</pawn>"
                        && reference.PawnPackage.Reference!.GlobalId == PawnGlobalIdUtility.Build("alice", source.InnerPawn), "Capture lost the dead pawn package or identity: " + path);
                    Check(!TradePawnUtility.IsPawnReference(reference), "Corpse package was classified as a live pawn sale.");
                    Check(reference.Metadata["clashofrim.core.state.rot"] == progress.ToString("R", CultureInfo.InvariantCulture), "Capture lost invariant rot state.");
                    Check(!source.Destroyed && source.InnerPawn.Dead && source.HitPoints == 51, "Capture changed the source corpse.");
                    foreach (string delivery in new[] { "caravan", "pod" })
                    {
                        delivered.Clear(); restores = corpseCreations = rotCallbacks = 0;
                        if (delivery == "caravan")
                        {
                            object?[] args = { caravan, new[] { reference }, 0, null };
                            Check((bool)AccessTools.Method(typeof(TradeCaravanFulfillmentUtility), "AddReceivedThings").Invoke(null, args)! && (int)args[2]! == 1, "Caravan corpse delivery failed.");
                        }
                        else
                        {
                            var item = new GiftItemReference(reference.GlobalKey, reference.DefName, 1, "snapshot", hitPoints: reference.HitPoints,
                                maxHitPoints: reference.MaxHitPoints, pawnPackage: reference.PawnPackage, metadata: reference.Metadata);
                            Check(AccessTools.Method(typeof(GiftLandingApplicator), "TryPrepareThings").Invoke(null, new object[] { item, "corpse-test", delivered }) is null, "Drop-pod corpse delivery failed.");
                        }
                        Check(delivered.Single() is Corpse restored && restored.InnerPawn.Dead && !ReferenceEquals(restored.InnerPawn, source.InnerPawn)
                            && restored.HitPoints == 51 && restored.Age == 543210 && restored.everBuriedInSarcophagus
                            && (int)AccessTools.Field(typeof(Corpse), "vanishAfterTimestamp").GetValue(restored)! == 987654
                            && restored.TryGetComp<CompRottable>().RotProgress == progress, "Delivery lost corpse or component state: " + path + "/" + delivery);
                        Check(restores == 1 && corpseCreations == 1 && rotCallbacks == (progress > 0 ? 1 : 0), "Delivery duplicated restoration or skipped stage-change handling.");
                    }
                }
            }
            var bad = new ModThingReferenceDto { DefName = corpseDef.defName, StackCount = 1 };
            Check(!TradeThingReferenceUtility.TryMakeThing(bad, 1, out var missing, out _) && missing is null, "Missing corpse package became an empty corpse.");
            bad.PawnPackage = new() { Status = new() { Dead = true } };
            restoreFails = true;
            Check(!TradeThingReferenceUtility.TryMakeThing(bad, 1, out _, out _), "Failed pawn restore fell back to an empty corpse.");
            restoreFails = false; race.race.corpseDef = derivedDef;
            Check(!TradeThingReferenceUtility.TryMakeThing(bad, 1, out _, out _), "Mismatched corpse definition was accepted.");
            bad.StackCount = 2;
            Check(!TradeThingReferenceUtility.TryMakeThing(bad, 1, out _, out _), "One pawn package was cloned into multiple corpses.");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        finally { restoreFails = false; harmony.UnpatchAll(harmony.Id); CultureInfo.CurrentCulture = culture; }
    }

    private static Pawn NewPawn()
    {
        var pawn = new Pawn { def = race, thingIDNumber = 81, kindDef = new PawnKindDef { defName = "CorpseTransferKind", race = race } };
        pawn.health = new Pawn_HealthTracker(pawn); pawn.health.SetDead();
        return pawn;
    }
    private static Corpse NewCorpse(Pawn pawn, float rot)
    {
        var corpse = (Corpse)Activator.CreateInstance(pawn.RaceProps.corpseDef.thingClass)!;
        corpse.def = pawn.RaceProps.corpseDef; corpse.stackCount = 1; corpse.thingIDNumber = 82; corpse.InnerPawn = pawn;
        var rottable = new CompRottable { parent = corpse, props = new CompProperties_Rottable() };
        AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(corpse, new List<ThingComp> { rottable });
        rottable.RotProgress = rot;
        return corpse;
    }
    private static bool Serialize(ref string __result) { __result = "<pawn>dead-pawn-state</pawn>"; return false; }
    private static bool PawnLabel(ref string __result) { __result = "茶色"; return false; }
    private static bool ItemLabel(ref string __result) { __result = "茶色 (死亡)"; return false; }
    private static bool Value(ref float __result) { __result = 0; return false; }
    private static bool Maximum(ref int __result) { __result = 100; return false; }
    private static bool TickManager(ref TickManager __result) { __result = ticks; return false; }
    private static bool Restore(ModPawnExchangePackageDto __0, ref Pawn? pawn, ref string message, ref bool __result)
    { Check(__0.Status?.Dead == true, "Restoring a live pawn as a corpse."); restores++; pawn = restoreFails ? null : NewPawn(); message = "fixture"; __result = !restoreFails; return false; }
    private static bool RejectLivePawnRestore() => throw new InvalidOperationException("Corpse was routed to live pawn restoration.");
    private static bool MakeCorpse(Pawn __instance, ref Corpse __result) { corpseCreations++; __result = NewCorpse(__instance, 0); return false; }
    private static bool RotStageChanged(Corpse __instance) { Check(__instance.InnerPawn?.Dead == true, "Rot callback ran before the inner pawn was restored."); rotCallbacks++; return false; }
    private static bool GiveThing(Thing __1) { delivered.Add(__1); return false; }
    private static bool Translate(string __0, ref string __result) { __result = __0; return false; }
    private static bool Skip() => false;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class DerivedCorpse : Corpse { }
}
