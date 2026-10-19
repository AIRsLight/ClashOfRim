using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Multiplayer;
using AIRsLight.ClashOfRim.ServerShop;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static class ClientShopListingRuntimeTests
{
    private static int estimates;
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        using var database = new DefDatabaseTestScope<ThingDef>();
        var harmony = new Harmony("ClashOfRim.Tests.ShopListingRuntime");
        var token = ClashOfRimCompatibilityApi.CreateRegistrationToken("shop-listing-runtime-tests");
        var defaults = new List<(string Surface, string? Inner, string? Definition)>();
        try
        {
            void Patch(MethodBase method, string name) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientShopListingRuntimeTests), name));
            Patch(AccessTools.PropertyGetter(typeof(ThingDef), "BaseMaxHitPoints"), nameof(MaxHp));
            Patch(AccessTools.Method(typeof(TradeUiUtility), nameof(TradeUiUtility.TryEstimateUnitMarketValue)), nameof(Estimate));
            Patch(AccessTools.Method(typeof(TradeUiUtility), nameof(TradeUiUtility.FormatMarketValue)), nameof(Format));
            ClashOfRimCompatibilityApi.UseRegistrationToken(token, () =>
            {
                ClashOfRimCompatibilityApi.RegisterThingReferenceDefaultMetadataProvider((surface, def, item) =>
                    defaults.Add((surface, item.MinifiedInnerDefName, def?.defName)));
                ClashOfRimCompatibilityApi.RegisterThingReferenceEditor("shop-listing-runtime-test", Draw,
                    (_, _, item) => item.Metadata.Remove("remove"), (_, _, item) => !item.Metadata.ContainsKey("incomplete"));
            });
            var category = new StuffCategoryDef { defName = "ShopEditorMetallic" };
            var steel = new ThingDef { defName = "ShopEditorSteel", label = "steel", thingClass = typeof(Thing), category = ThingCategory.Item,
                stuffProps = new() { categories = new() { category } } };
            var shell = new ThingDef { defName = "ShopEditorMinified", label = "wrapper", thingClass = typeof(MinifiedThing), category = ThingCategory.Item };
            var building = new ThingDef { defName = "ShopEditorBuilding", label = "building", thingClass = typeof(Building), category = ThingCategory.Building,
                minifiedDef = shell, useHitPoints = true, stuffCategories = new() { category }, comps = new() { new() { compClass = typeof(CompQuality) } } };
            var plain = new ThingDef { defName = "ShopEditorPlain", label = "普通", thingClass = typeof(Thing), category = ThingCategory.Item };
            var definitions = Enumerable.Range(0, 130).Select(i => new ThingDef { defName = "ShopEditorItem" + i, label = "entry " + i,
                thingClass = typeof(Thing), category = ThingCategory.Item }).ToList();
            definitions.AddRange(new[] { steel, shell, building, plain,
                new ThingDef { defName = "ShopEditorCorpse", label = "corpse", thingClass = typeof(Corpse), category = ThingCategory.Item } });
            database.Replace(definitions);
            var runtime = new ClientShopListingRuntime(); var catalog = runtime.Catalog;
            Check(catalog.Count == 133 && catalog.Any(d => d.DefName == building.defName) && !catalog.Any(d => d.DefName == shell.defName)
                && !catalog.Any(d => d.DefName == "ShopEditorCorpse"), "Engine catalog changed corpse/wrapper/packable filtering.");
            Check(ReferenceEquals(catalog, runtime.Catalog), "Unchanged engine catalog rebuilt each read.");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture.Name == "tr-TR" ? "zh-CN" : "tr-TR");
            Check(!ReferenceEquals(catalog, runtime.Catalog), "Culture change kept the old ordered/localized catalog.");
            var refreshed = runtime.Catalog;
            DefDatabase<ThingDef>.Add(new ThingDef { defName = "ShopEditorOtherCorpse", label = "corpse", thingClass = typeof(Corpse), category = ThingCategory.Item });
            Check(!ReferenceEquals(refreshed, runtime.Catalog) && runtime.Catalog.Count == 133,
                "Changed definition source retained old descriptors or stopped filtering corpses.");
            foreach (string surface in new[] { ThingReferenceSurfaces.ServerShopListing, ThingReferenceSurfaces.TradeRequest })
            {
                int count = defaults.Count; var item = runtime.Create(building.defName, surface);
                Check(defaults.Count == count + 1 && defaults.Last() == (surface, building.defName, building.defName),
                    "Extension defaults did not receive one complete minified identity and actual surface.");
                Check(item.DefName == shell.defName && item.MinifiedInnerDefName == building.defName && item.HitPoints == 200
                    && item.MinifiedInnerHitPoints == 200 && item.Quality == "Normal" && item.MinifiedInnerQuality == "Normal",
                    "Engine template lost wrapper, quality or durability defaults.");
                var features = runtime.Features(item);
                Check(features is { Stuff: true, Quality: true, HitPoints: true, HitPointsPercent: 100 }
                    && runtime.Materials(item).Single().DefName == steel.defName && runtime.HitPointsFromPercent(item, 73) == 146,
                    "Engine options used the wrapper instead of its contents.");
                item.Metadata["remove"] = "x"; runtime.Normalize(item, surface);
                Check(!item.Metadata.ContainsKey("remove") && runtime.IsComplete(item, surface), "Editor bypassed extension cleaner/completeness.");
                item.Metadata["incomplete"] = "x"; Check(!runtime.IsComplete(item, surface), "Extension veto was ignored.");
                item.StackCount = 3; estimates = 0; var value = runtime.Estimate(item);
                Check(estimates == 1 && value.SuggestedPrice == 8 && value.UnitLabel == "unit:2.5", "Suggestion/display repeated estimation or changed batch rounding.");
            }
            var ordinary = runtime.Create(plain.defName, ThingReferenceSurfaces.ServerShopListing); ordinary.StuffDefName = steel.defName;
            runtime.Normalize(ordinary, ThingReferenceSurfaces.ServerShopListing);
            Check(ordinary.StuffDefName is null && !runtime.Features(ordinary).Stuff && runtime.Materials(ordinary).Count == 0,
                "Unsupported material survived normalization.");
            BridgeEntryPoints(building, steel);
        }
        finally
        {
            ClashOfRimCompatibilityApi.RevokeRegistrationToken(token); harmony.UnpatchAll(harmony.Id);
            CultureInfo.CurrentCulture = culture;
        }
    }
    private static void BridgeEntryPoints(ThingDef building, ThingDef steel)
    {
        object? Invoke(string name, params object?[] args) => typeof(ServerShopListingDialogWindow)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
        var offered = ((IEnumerable<ThingDef>)Invoke("SearchThingDefs", "ShopEditor")!).ToArray();
        Check(offered.Length == 133 && offered.Contains(building), "Legacy RimTest search disappeared or inherited the UI cap.");
        var item = (ModThingReferenceDto)Invoke("BuildItemReference", building)!;
        Invoke("SetItemStuff", item, steel.defName); Invoke("SetItemQuality", item, "Excellent"); Invoke("SetItemHitPoints", item, 146);
        Check(item.MinifiedInnerStuffDefName == steel.defName && item.MinifiedInnerQuality == "Excellent" && item.MinifiedInnerHitPoints == 146
            && (bool)Invoke("HasCompleteItem", item, ThingReferenceSurfaces.ServerShopListing)!, "RimTest entry points diverged from extracted editing.");
    }
    private static bool MaxHp(ref int __result) { __result = 200; return false; }
    private static bool Estimate(ref float marketValue, ref bool __result) { estimates++; marketValue = 2.5f; __result = true; return false; }
    private static bool Format(float marketValue, ref string __result) { __result = "unit:" + marketValue.ToString(CultureInfo.InvariantCulture); return false; }
    private static bool Draw(string surface, ThingDef? def, ModThingReferenceDto item, Rect rect, out float consumedHeight)
    { consumedHeight = 0; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
