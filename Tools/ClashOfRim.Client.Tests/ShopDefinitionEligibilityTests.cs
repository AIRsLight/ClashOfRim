using System;
using System.Reflection;
using AIRsLight.ClashOfRim;
using RimWorld;
using Verse;

internal static class ShopDefinitionEligibilityTests
{
    public static void Run()
    {
        var assembly = typeof(ClashOfRimMod).Assembly;
        var eligibility = assembly.GetType("AIRsLight.ClashOfRim.Trades.TradeThingReferenceUtility", true)!.GetMethod("IsTradeableItemDef")!;
        foreach (var shell in new[] { typeof(MinifiedThing), typeof(MinifiedTree) })
        {
            var def = new ThingDef { defName = shell.Name, thingClass = shell, category = ThingCategory.Item };
            if ((bool)eligibility.Invoke(null, new object[] { def })!) throw new Exception("Empty minified wrapper offered as a standalone item.");
        }
        var tree = new ThingDef { defName = "TestTree", thingClass = typeof(Plant), category = ThingCategory.Plant, minifiedDef = new ThingDef { defName = "MinifiedTree" } };
        var packable = assembly.GetType("AIRsLight.ClashOfRim.Trades.TradeThingReferenceUtility", true)!.GetMethod("IsPackableItemDef")!;
        DefDatabase<ThingDef>.Add(tree);
        var baseline = (System.Collections.IEnumerable)typeof(ClashOfRimMod).GetMethod("ReadCurrentPackableBuildings", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        bool found = false;
        foreach (var entry in baseline) if ((string)entry.GetType().GetProperty("DefName")!.GetValue(entry)! == tree.defName) found = true;
        if (!found) throw new Exception("Packable tree missing from authoritative transfer whitelist.");
        if (!(bool)packable.Invoke(null, new object[] { tree })!) throw new Exception("Packable tree did not receive an inner item template.");
    }
}
