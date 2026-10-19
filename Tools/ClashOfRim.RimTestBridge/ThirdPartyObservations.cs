using System.Collections;
using System.Diagnostics;
using System.Xml.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal static class ThirdPartyObservations
{
    private static readonly HashSet<string> restored = new(StringComparer.Ordinal);
    private static bool installed;
    internal static object Statue(Thing thing)
    {
        var pawn = BridgeReflection.Field<Pawn>(thing.TryGetComp<CompStatue>(), "fakePawn");
        if (pawn is null) return new { defName = thing.def.defName, race = "missing-display-pawn", stateXml = StatueState(thing.TryGetComp<CompStatue>()) };
        var alien = pawn.AllComps.FirstOrDefault(c => c.GetType().Name == "AlienComp");
        return new { defName = thing.def.defName, race = pawn.def.defName, kind = pawn.kindDef.defName, name = pawn.Name?.ToStringFull,
            gender = pawn.gender.ToString(), body = pawn.story.bodyType.defName, head = pawn.story.headType.defName, hair = pawn.story.hairDef.defName,
            hairColor = Color(pawn.story.HairColor), skinColor = Color(pawn.story.SkinColor), stateXml = StatueState(thing.TryGetComp<CompStatue>()),
            faces = FacialControllers(pawn)
                .OrderBy(c => c.GetType().FullName, StringComparer.Ordinal)
                .Select(c => new { type = c.GetType().FullName, face = AccessTools.PropertyGetter(c.GetType(), "FaceTypeDefName").Invoke(c, null) }).ToArray(),
            variants = alien is null ? null : (AccessTools.Field(alien.GetType(), "addonVariants")?.GetValue(alien) as IEnumerable)?.Cast<object>().Select(x => x.ToString()).ToArray() };
    }
    private static float[] Color(UnityEngine.Color c) => [c.r, c.g, c.b, c.a];
    internal static IEnumerable<ThingComp> FacialControllers(Pawn pawn) =>
        pawn.AllComps.Where(c => c.GetType().GetInterfaces().Any(i => i.FullName == "FacialAnimation.IFacialAnimationController"));
    private static string StatueState(CompStatue comp)
    {
        if (Scribe.mode != LoadSaveMode.Inactive) throw new InvalidOperationException("Statue observation requires inactive Scribe.");
        var path = Path.Combine(Path.GetTempPath(), "rimtest-statue-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Scribe.saver.InitSaving(path, "statue"); comp.PostExposeData(); Scribe.saver.FinalizeSaving();
            var xml = XDocument.Load(path); xml.Root!.Element("taleRef")?.Remove();
            return xml.Root.ToString(SaveOptions.DisableFormatting);
        }
        finally { if (Scribe.mode != LoadSaveMode.Inactive) Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }
    internal static void Install()
    {
        if (installed) return;
        var method = AccessTools.Method(AccessTools.TypeByName("MVCF.VerbManager"), "Initialize");
        if (method is null) throw new InvalidOperationException("MVCF Initialize missing.");
        new Harmony("ClashOfRim.RimTestBridge.ThirdPartyProbe").Patch(method, postfix: new HarmonyMethod(typeof(ThirdPartyObservations), nameof(AfterInitialize)));
        installed = true;
    }
    private static void AfterInitialize(Pawn __0)
    {
        if (new StackTrace().GetFrames().Any(f => f.GetMethod()?.Name == "RestoreMvcfPawnVerbManager")) restored.Add(__0.ThingID);
    }
    internal static object Pawn(Pawn pawn)
    {
        var utility = AccessTools.TypeByName("MVCF.Utilities.PawnVerbUtility");
        var table = AccessTools.Field(utility, "managers").GetValue(null)!;
        object?[] args = [pawn, null];
        bool present = (bool)AccessTools.Method(table.GetType(), "TryGetValue").Invoke(table, args)!;
        var manager = present ? AccessTools.Field(args[1]!.GetType(), "Value").GetValue(args[1]) : null;
        var verbs = manager is null ? [] : ((IEnumerable)AccessTools.PropertyGetter(manager.GetType(), "AllVerbs").Invoke(manager, null)!).Cast<Verb>().ToArray();
        return new { pawn.ThingID, managerPresent = manager is not null, initializedByCompatibility = restored.Contains(pawn.ThingID),
            ownerMatches = manager is not null && ReferenceEquals(AccessTools.PropertyGetter(manager.GetType(), "Pawn").Invoke(manager, null), pawn),
            verbCount = verbs.Length, nullVerbs = verbs.Count(v => v is null), verbOwnersMatch = verbs.All(v => v is not null && ReferenceEquals(v.CasterPawn, pawn)) };
    }
}
