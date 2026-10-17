using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.ServerShop;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using HarmonyLib;
using RimWorld;
using Verse;

// Exercise the real shop engine adapter with controlled game API edges. Inventory
// matching/removal and landing already have their own suites; these patches observe
// which map, quantities, item state and mutation boundary the adapter passes to them.
internal static class ClientShopRuntimeTests
{
    private static readonly List<Map> maps = new();
    private static readonly List<Thing> inventory = new();
    private static readonly List<string> trace = new();
    private static Map home = null!, displayed = null!;
    private static ModThingReferenceDto? requirement;
    private static GiftLandingPlan? landing;
    private static Map? landedMap;
    private static string modes = "", fault = "";
    private static int silver, consumed;

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.ShopRuntime");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientShopRuntimeTests), prefix));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.Maps)), nameof(ReadMaps));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap)), nameof(ReadDisplayedMap));
        Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.IsPlayerHome)), nameof(IsHome));
        Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map)), nameof(ThingMap));
        Patch(AccessTools.Method(typeof(ClientBankRuntime), nameof(ClientBankRuntime.CountPlayerHomeSilver)), nameof(Silver));
        Patch(AccessTools.Method(typeof(ClientBankRuntime), nameof(ClientBankRuntime.TryConsumePlayerHomeSilver)), nameof(Consume));
        Patch(AccessTools.Method(typeof(GiftLandingApplicator), nameof(GiftLandingApplicator.ApplyToMap)), nameof(Land));
        Patch(AccessTools.Method(typeof(TradeInventoryUtility), nameof(TradeInventoryUtility.AccessibleMapItems)), nameof(Inventory));
        Patch(AccessTools.Method(typeof(TradeThingReferenceUtility), nameof(TradeThingReferenceUtility.MatchesRequirement),
            new[] { typeof(ModThingReferenceDto), typeof(Thing), typeof(string), typeof(string) }), nameof(Match));
        Patch(AccessTools.Method(typeof(TradeThingReferenceUtility), nameof(TradeThingReferenceUtility.BuildThingReference)), nameof(Reference));
        Patch(AccessTools.Method(typeof(TradeMapFulfillmentUtility), nameof(TradeMapFulfillmentUtility.RemoveThingCount)), nameof(Remove));
        Patch(AccessTools.Method(typeof(ClashOfRimCompatibilityApi), nameof(ClashOfRimCompatibilityApi.NormalizeThingReferenceMetadata)), nameof(Skip));
        Patch(AccessTools.Method(typeof(TradeUiUtility), nameof(TradeUiUtility.ThingLabel), new[] { typeof(ModThingReferenceDto), typeof(bool), typeof(string), typeof(string), typeof(int?), typeof(string) }), nameof(Label));
        Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string) }), nameof(Text));
        Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string), typeof(NamedArgument[]) }), nameof(Text));
        var originalSilver = ThingDefOf.Silver;
        try
        {
            ThingDefOf.Silver = new() { defName = "Silver" };
            foreach (bool sale in new[] { false, true })
            foreach (string failure in new[] { "", "landing", "consume", "shrink", "reference" })
            {
                if (sale ? failure == "consume" : failure is "shrink" or "reference") continue;
                Reset(); fault = failure;
                var command = Command(sale);
                var source = new ClientShopRuntime().Capture();
                Require(source.Validate(command) is null, "Shop rejected valid test inventory.");
                ClientShopLocalResult? result = null;
                try { result = source.Apply(new("http://server", "owner", "colony", "snapshot", null, null, "auth"), command, () => trace.Add("mutation")); }
                catch (InvalidOperationException) when (failure == "reference") { }
                if (failure == "reference")
                { Require(!trace.Contains("mutation") && landing is null, "Outbound serialization failed after starting payment."); continue; }
                Require(ReferenceEquals(landedMap, home) && !ReferenceEquals(landedMap, displayed) && landing!.TargetMapUniqueId == "Map_7",
                    "Shop landed on the displayed remote map instead of its captured home map.");
                Require(trace.IndexOf("mutation") >= 0 && trace.IndexOf("mutation") < trace.IndexOf("landing"), "Shop reported mutation only after landing.");
                Require(result!.Success == (failure == ""), "Partial local failure was reported as a completed exchange: " + failure);
                if (!sale)
                {
                    var item = landing!.Items.Single();
                    Require(item.StackCount == 6 && item.Metadata["state"] == "original" && item.ThingPackage!.Scribe!.XmlGzipBase64 == "payload"
                        && item.UniqueWeaponTraits.SequenceEqual(new[] { "trait" }), "Delivery plan dropped complex item fields or batch quantity.");
                    Require(consumed == (failure == "landing" ? 0 : 20), "Shop charged the wrong amount or charged after rejected landing.");
                }
                else
                {
                    Require(landing!.Items.Single().DefName == "Silver" && landing.Items.Single().StackCount == 20
                        && requirement!.StackCount == 6 && requirement.UniqueWeaponTraits.SequenceEqual(new[] { "trait" })
                        && modes == "AtMost/AtLeast", "Sale changed quantity, requirement metadata or payout semantics.");
                    if (failure == "") Require(result.Delivered.Sum(t => t.StackCount) == 6 && inventory.Sum(t => t.stackCount) == 1
                        && result.Delivered.All(t => t.GlobalKey.StartsWith("owner:owner/colony:colony/snapshot:snapshot/shop:listing/thing:", StringComparison.Ordinal)), "Sale did not use captured identity or exact removal quantities.");
                    else Require(!trace.Contains("remove"), "Failed payment/inventory validation continued removing things.");
                }
            }
            Reset(); var captured = new ClientShopRuntime().Capture(); maps.Remove(home);
            Require(captured.Validate(Command(false))!.Contains("StatusNoMap"), "Captured source accepted an unloaded map.");
            Reset(); silver = 0;
            Require(new ClientShopRuntime().Capture().Validate(Command(false))!.Contains("StatusInsufficientSilver"), "Purchase ignored current shared bank balance.");
            Reset(); inventory.Clear(); var missing = new ClientShopRuntime().Capture();
            var rejected = missing.Apply(new("http://server", "owner", "colony", "snapshot", null, null, "auth"), Command(true), () => trace.Add("mutation"));
            Require(!rejected.Success && !trace.Contains("mutation") && landing is null, "Insufficient sale inventory started a payment.");
        }
        finally { ThingDefOf.Silver = originalSilver; harmony.UnpatchAll(harmony.Id); maps.Clear(); inventory.Clear(); trace.Clear(); }
    }

    private static void Reset()
    {
        home = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); home.uniqueID = 7;
        displayed = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map)); displayed.uniqueID = 8;
        maps.Clear(); maps.Add(home); maps.Add(displayed);
        inventory.Clear();
        inventory.Add(new Thing { def = new() { defName = "Steel" }, thingIDNumber = 1, stackCount = 2 });
        inventory.Add(new Thing { def = new() { defName = "Steel" }, thingIDNumber = 2, stackCount = 5 });
        trace.Clear(); requirement = null; landing = null; landedMap = null; modes = fault = ""; silver = 100; consumed = 0;
    }
    private static ClientShopCommand Command(bool sale) => new("key", new() { ListingId = "listing", ListingKind = sale ? "BuyFromPlayer" : "SellToPlayer",
        QualityRequirementMode = "AtMost", HitPointsRequirementMode = "unknown", Item = new() { DefName = "Steel", StackCount = 3,
            Metadata = new() { ["state"] = "original" }, UniqueWeapon = true, UniqueWeaponTraits = new() { "trait" },
            ThingPackage = new() { Scribe = new() { XmlGzipBase64 = "payload" } } } }, 2, 20, 6, Array.Empty<ModThingReferenceDto>());
    private static bool ReadMaps(ref List<Map> __result) { __result = maps; return false; }
    private static bool ReadDisplayedMap(ref Map __result) { __result = displayed; return false; }
    private static bool IsHome(Map __instance, ref bool __result) { __result = ReferenceEquals(__instance, home); return false; }
    private static bool ThingMap(ref Map __result) { __result = home; return false; }
    private static bool Silver(ref int __result) { __result = silver; return false; }
    private static bool Consume(int amount, out string message, ref bool __result)
    { consumed += amount; trace.Add("consume"); message = "consume failed"; __result = fault != "consume"; return false; }
    private static bool Land(GiftLandingPlan plan, Map map, ref GiftLandingApplicationResult __result)
    {
        landing = plan; landedMap = map; trace.Add("landing");
        if (fault == "shrink") inventory[0].stackCount = 0;
        __result = fault == "landing" ? GiftLandingApplicationResult.Failed(GiftLandingApplicationResultKind.PlacementFailed, plan.EventId, "landing failed")
            : GiftLandingApplicationResult.Applied(plan.EventId, 1, 1, "DropPod", true);
        return false;
    }
    private static bool Inventory(ref IEnumerable<Thing> __result) { __result = inventory; return false; }
    private static bool Match(ModThingReferenceDto __0, string? qualityRequirementMode, string? hitPointsRequirementMode, ref bool __result)
    { requirement = __0; modes = qualityRequirementMode + "/" + hitPointsRequirementMode; __result = true; return false; }
    private static bool Reference(string globalKey, int count, string? biocodedPawnGlobalId, string surface, ref ModThingReferenceDto __result)
    {
        if (fault == "reference") throw new InvalidOperationException("outbound serialization");
        Require(surface == ThingReferenceSurfaces.ServerShopSale && biocodedPawnGlobalId is null, "Sale fabricated a pawn identity or changed its transfer surface.");
        __result = new() { GlobalKey = globalKey, DefName = "Steel", StackCount = count }; return false;
    }
    private static bool Remove(Thing thing, int count) { trace.Add("remove"); thing.stackCount -= count; return false; }
    private static bool Skip() => false;
    private static bool Label(ref string __result) { __result = "item"; return false; }
    private static bool Text(string __0, ref string __result) { __result = __0; return false; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
