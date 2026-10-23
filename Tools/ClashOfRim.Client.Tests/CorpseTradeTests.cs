using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Trades;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static class CorpseTradeTests
{
    private static int constructions, restorations, confirmations, windows;
    private static string confirmation = "";

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.CorpseTrade");
        var culture = CultureInfo.CurrentCulture;
        using var definitions = new DefDatabaseTestScope<ThingDef>();
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(CorpseTradeTests), prefix));
        Patch(AccessTools.Method(typeof(ThingMaker), nameof(ThingMaker.MakeThing)), nameof(MakeThing));
        Patch(AccessTools.Method(typeof(TradeThingReferenceUtility), nameof(TradeThingReferenceUtility.TryMakeThing)), nameof(NoteRestoration));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClashOfRimText)).Where(m => m.Name == nameof(ClashOfRimText.Key))) Patch(method, nameof(Translate));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WindowStack)), nameof(WindowStack));
        Patch(AccessTools.Method(typeof(WindowStack), nameof(Verse.WindowStack.Add)), nameof(AddWindow));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Dialog_MessageBox)).Where(m => m.Name == nameof(Dialog_MessageBox.CreateConfirmation))) Patch(method, nameof(Confirm));
        Patch(AccessTools.Method(typeof(TradeUiUtility), nameof(TradeUiUtility.FormatMarketValue)), nameof(FormatValue));
        try
        {
            var corpseDef = new ThingDef { defName = "CorpseTradeTest", label = "test corpse", category = ThingCategory.Item, thingClass = typeof(Corpse), stackLimit = 1 };
            definitions.Replace(new[] { corpseDef });
            using var fixture = new ClientTradeMarketTests.Fixture();
            var actions = new ClientTradeActions(null!, fixture.State, fixture.Session, null!, () => Array.Empty<ModWorldMapMarkerDto>(), () => null, () => null);
            var window = new TradeOrderDialogWindow(actions, new Map());
            foreach (string locale in new[] { "zh-CN", "en-US", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(locale);
                var offered = new[] { Reference(corpseDef, "茶色", 180000), Reference(corpseDef, "传承", 180000), Reference(corpseDef, "米戈", 50000000), Reference(corpseDef, "里图", 0) };
                offered[1].PawnPackage = new() { Status = new() { Dead = true } };
                offered[2].PawnPackageId = "stored-corpse-pawn";
                constructions = restorations = confirmations = windows = 0;
                var draft = new TradeOrderDraft(Array.Empty<TradeOfferSelection>(), offered, Array.Empty<ModThingReferenceDto>(), 6, false, true, true);
                AccessTools.Method(typeof(TradeOrderDialogWindow), "ShowCreateConfirmation").Invoke(window, new object[] { draft });
                Check(confirmations == 1 && windows == 1 && offered.All(r => confirmation.Contains(r.DisplayLabel!)), "Publishing multiple corpses did not display their names in the confirmation.");
                foreach (var reference in offered)
                {
                    object?[] args = { reference, null };
                    Check(!(bool)AccessTools.Method(typeof(TradeUiUtility), "TryGetConcreteIconThing").Invoke(null, args)!, "Corpse icon constructed a temporary corpse.");
                    Check(TradeUiUtility.TryEstimateUnitMarketValue(reference, out float value) && value == reference.MarketValue, "Corpse estimate did not preserve captured market value.");
                }
                Check(constructions == 0 && restorations == 0, "Corpse presentation invoked transfer reconstruction.");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        finally { harmony.UnpatchAll(harmony.Id); CultureInfo.CurrentCulture = culture; }
        CorpseTransferTests.Run();
    }

    private static ModThingReferenceDto Reference(ThingDef def, string name, float rot) => new()
    {
        GlobalKey = name, DefName = def.defName, StackCount = 1, DisplayLabel = name + " (死亡)", MarketValue = rot == 0 ? 42.5f : 0,
        Metadata = new() { ["clashofrim.core.state.rot"] = rot.ToString(CultureInfo.InvariantCulture) }
    };

    private static bool MakeThing(ThingDef __0, ref Thing __result)
    {
        constructions++;
        var corpse = new Corpse { def = __0 };
        AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(corpse, new List<ThingComp> { new CompRottable { parent = corpse, props = new CompProperties_Rottable() } });
        __result = corpse;
        return false;
    }

    private static void NoteRestoration() => restorations++;

    private static bool Translate(object[] __args, ref string __result)
    {
        __result = string.Join(" ", __args.Select(a => a is NamedArgument[] named ? string.Join(" ", named.Select(n => n.arg?.ToString())) : a?.ToString()));
        return false;
    }
    private static bool WindowStack(ref WindowStack __result) { __result = (WindowStack)RuntimeHelpers.GetUninitializedObject(typeof(WindowStack)); return false; }
    private static bool AddWindow(Window __0) { Check(__0 is Dialog_MessageBox, "Unexpected confirmation window."); windows++; return false; }
    private static bool Confirm(object[] __args, ref Dialog_MessageBox __result)
    { confirmations++; confirmation = __args[0].ToString()!; __result = (Dialog_MessageBox)RuntimeHelpers.GetUninitializedObject(typeof(Dialog_MessageBox)); return false; }
    private static bool FormatValue(float __0, ref string __result) { __result = __0.ToString(CultureInfo.InvariantCulture); return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
