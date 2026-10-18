using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.Admin;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class ClientAdminBaselineRuntimeTests
{
    private static List<ThingDef> definitions = new();
    private static readonly HashSet<StatDef> failed = new();
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture; var market = StatDefOf.MarketValue; var hp = StatDefOf.MaxHitPoints;
        var game = Current.Game; var harmony = new Harmony("ClashOfRim.Tests.AdminBaselineRuntime");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(DefDatabase<ThingDef>), "AllDefsListForReading"), prefix: new HarmonyMethod(typeof(ClientAdminBaselineRuntimeTests), nameof(Definitions)));
            harmony.Patch(AccessTools.Method(typeof(StatExtension), nameof(StatExtension.GetStatValueAbstract), new[] { typeof(BuildableDef), typeof(StatDef), typeof(ThingDef) }), prefix: new HarmonyMethod(typeof(ClientAdminBaselineRuntimeTests), nameof(Stat)));
            Current.Game = null;
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); Catalogs();
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); CultureInfo.CurrentCulture = culture; StatDefOf.MarketValue = market; StatDefOf.MaxHitPoints = hp;
            Current.Game = game; definitions.Clear(); failed.Clear();
        }
    }
    private static void Catalogs()
    {
        failed.Clear();
        var quality = new StatPart_Quality();
        AccessTools.Field(typeof(StatPart_Quality), "factorNormal").SetValue(quality, 1.25f);
        AccessTools.Field(typeof(StatPart_Quality), "maxGainNormal").SetValue(quality, 37.5f);
        StatDefOf.MarketValue = new() { defName = "MarketValue", parts = new() { quality } };
        StatDefOf.MaxHitPoints = new() { defName = "MaxHitPoints" };
        var category = new StuffCategoryDef { defName = "Metallic" };
        ThingDef Stuff(string name, bool compatible = true) => new()
        {
            defName = name, label = "材料 İ", category = ThingCategory.Item,
            stuffProps = new() { categories = compatible ? new() { category } : new() }
        };
        var useful = Stuff("useful");
        useful.stuffProps.statFactors = new() { new() { stat = StatDefOf.MaxHitPoints, value = 1.5f } };
        useful.stuffProps.statOffsets = new() { new() { stat = StatDefOf.MaxHitPoints, value = -3.5f } };
        var building = new ThingDef { defName = "building", category = ThingCategory.Building, useHitPoints = true,
            stuffCategories = new() { category }, minifiedDef = new() { defName = "packed" } };
        var fallback = new ThingDef { defName = "fallback", category = ThingCategory.Building, useHitPoints = true,
            statBases = new() { new() { stat = StatDefOf.MarketValue, value = 7.5f }, new() { stat = StatDefOf.MaxHitPoints, value = 17 } } };
        var negative = new ThingDef { defName = "negative", category = ThingCategory.Item };
        var pawn = new ThingDef { defName = "pawn", category = ThingCategory.Pawn, stuffCategories = new() { category } };
        var invulnerable = new ThingDef { defName = "invulnerable", category = ThingCategory.Building, useHitPoints = false };
        var trap = new ThingDef { defName = "auto", thingClass = typeof(Building_Trap), category = ThingCategory.Building };
        var candidate = new ThingDef { defName = "candidateTrap", thingClass = typeof(Thing), category = ThingCategory.Item };
        definitions = new() { useful, Stuff("same"), Stuff("incompatible", false), building, fallback, negative, pawn, invulnerable, trap, candidate, new() { defName = "" }, null! };
        var runtime = new ClientAdminBaselineRuntime();
        var values = runtime.ReadMarketValues();
        Check(values.Count == 10 && values.Select(v => v.DefName).SequenceEqual(values.Select(v => v.DefName).OrderBy(v => v, StringComparer.Ordinal)), "Market capture lost filtering/ordinal order.");
        Check(values.Single(v => v.DefName == "building").MarketValue == 12.5f && values.Single(v => v.DefName == "negative").MarketValue == 0
            && values.Single(v => v.DefName == "fallback").MarketValue == 7.5f, "Market stat clamp/fallback changed.");
        var pairs = runtime.ReadStuffMarketValues();
        Check(pairs.Count == 1 && pairs[0].ThingDefName == "building" && pairs[0].StuffDefName == "useful" && pairs[0].MarketValue == 18.75f,
            "Stuff market capture lost compatible/equal-value/pawn filtering.");
        var packed = runtime.ReadPackableBuildings();
        Check(packed.Count == 1 && packed[0].DefName == "building" && packed[0].MinifiedDefName == "packed", "Packable building metadata changed.");
        var buildings = runtime.ReadBuildings();
        Check(buildings.Count == 4 && buildings.Single(v => v.DefName == "building").EstimatedMaxHitPoints == 124
            && buildings.Single(v => v.DefName == "fallback").EstimatedMaxHitPoints == 17
            && buildings.Single(v => v.DefName == "invulnerable").EstimatedMaxHitPoints == 0, "Building HP estimate/fallback changed.");
        var modifiers = runtime.ReadStuffHitPointModifiers();
        Check(modifiers.Count == 1 && modifiers[0].DefName == "useful" && modifiers[0].MaxHitPointsFactor == 1.5f && modifiers[0].MaxHitPointsOffset == -3.5f,
            "Stuff HP modifiers lost defaults, filtering or invariant floats.");
        var qualities = runtime.ReadQualityModifiers();
        Check(qualities.Count == Enum.GetValues<QualityCategory>().Length && qualities.Single(q => q.Quality == "Normal").Factor == 1.25f
            && qualities.Single(q => q.Quality == "Normal").MaxGain == 37.5f, "Quality field lookup changed.");
        StatDefOf.MarketValue.parts.Clear(); Check(runtime.ReadQualityModifiers().Count == 0, "Missing quality part did not produce an empty catalog.");
        var traps = runtime.ScanTraps();
        Check(traps.Count == 2 && traps.Single(t => t.DefName == "auto").Status == TrapClassificationScanStatus.ApprovedByInheritance
            && traps.Single(t => t.DefName == "candidateTrap").Status == TrapClassificationScanStatus.CandidateRequiresApproval, "Runtime stopped using the shared trap classifier.");
    }
    private static bool Definitions(ref List<ThingDef> __result) { __result = definitions; return false; }
    private static bool Stat(BuildableDef __0, StatDef __1, ThingDef? __2, ref float __result)
    {
        if (__0.defName == "fallback")
        {
            if (failed.Add(__1)) throw new InvalidOperationException("stat worker failed");
            __result = __1 == StatDefOf.MaxHitPoints ? 17 : 7.5f; return false;
        }
        __result = __1 == StatDefOf.MaxHitPoints ? 123.6f : __0.defName == "negative" ? -2.5f : __2?.defName == "useful" ? 18.75f : 12.5f;
        return false;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
