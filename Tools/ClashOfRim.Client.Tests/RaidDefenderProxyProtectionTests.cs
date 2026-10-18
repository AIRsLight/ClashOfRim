using System;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class RaidDefenderProxyProtectionTests
{
    private static bool dead, destroyed, hasTorso, recruitable;
    private static Faction? faction;
    private static HediffDef? protection;
    private static BodyPartRecord torso = null!;
    private static int factionChanges, added;

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.RaidDefenderProxyProtection");
        void Getter(Type type, string property, string prefix) => harmony.Patch(AccessTools.PropertyGetter(type, property),
            prefix: new HarmonyMethod(typeof(RaidDefenderProxyProtectionTests), prefix));
        try
        {
            // Keep engine state access isolated while executing the real protection and duplicate-hediff checks.
            Getter(typeof(Pawn), nameof(Pawn.Dead), nameof(Dead));
            Getter(typeof(Thing), nameof(Thing.Destroyed), nameof(Destroyed));
            Getter(typeof(Thing), nameof(Thing.Faction), nameof(FactionValue));
            Getter(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.Recruitable), nameof(Recruitable));
            harmony.Patch(AccessTools.PropertySetter(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.Recruitable)),
                prefix: new HarmonyMethod(typeof(RaidDefenderProxyProtectionTests), nameof(SetRecruitable)));
            Getter(typeof(RaidDefenderLootProtectionUtility), nameof(RaidDefenderLootProtectionUtility.ProtectionHediff), nameof(Protection));
            harmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.SetFaction)), prefix: new HarmonyMethod(typeof(RaidDefenderProxyProtectionTests), nameof(SetFaction)));
            harmony.Patch(AccessTools.Method(typeof(HediffSet), nameof(HediffSet.TryGetBodyPartRecord)), prefix: new HarmonyMethod(typeof(RaidDefenderProxyProtectionTests), nameof(BodyPart)));
            harmony.Patch(AccessTools.Method(typeof(HediffMaker), nameof(HediffMaker.MakeHediff)), prefix: new HarmonyMethod(typeof(RaidDefenderProxyProtectionTests), nameof(MakeHediff)));
            var add = AccessTools.GetDeclaredMethods(typeof(Pawn_HealthTracker))
                .Single(m => m.Name == nameof(Pawn_HealthTracker.AddHediff) && m.GetParameters()[0].ParameterType == typeof(Hediff));
            harmony.Patch(add, prefix: new HarmonyMethod(typeof(RaidDefenderProxyProtectionTests), nameof(AddHediff)));
            var defender = New<Faction>(); var other = New<Faction>(); var def = new HediffDef { defName = "protection" };
            var pawn = new Pawn { def = new ThingDef { defName = "Human", race = new RaceProperties() } };
            pawn.health = new Pawn_HealthTracker(pawn); pawn.guest = new Pawn_GuestTracker(pawn);
            torso = new BodyPartRecord();
            void Reset()
            {
                dead = destroyed = false; hasTorso = true; faction = other; protection = def;
                factionChanges = added = 0; pawn.health.hediffSet.hediffs.Clear(); pawn.guest.Recruitable = true;
            }
            foreach (string invalid in new[] { "pawn", "faction", "dead", "destroyed" })
            {
                Reset(); dead = invalid == "dead"; destroyed = invalid == "destroyed";
                ClashOfRimCompatibilityApi.ApplyRaidDefenderProxyPawnProtection(invalid == "pawn" ? null! : pawn, invalid == "faction" ? null! : defender);
                Check(factionChanges == 0 && added == 0 && pawn.guest.Recruitable, "Invalid defender protection changed faction, recruitment or health.");
            }
            Reset();
            ClashOfRimCompatibilityApi.ApplyRaidDefenderProxyPawnProtection(pawn, defender);
            Check(ReferenceEquals(faction, defender) && factionChanges == 1 && !pawn.guest.Recruitable && added == 1
                && RaidDefenderLootProtectionUtility.HasProtectionHediff(pawn), "Proxy protection lost faction, recruitment or protection effects.");
            RaidDefenderLootProtectionUtility.ApplyProxyPawnProtection(pawn, defender);
            Check(factionChanges == 1 && added == 1 && pawn.health.hediffSet.hediffs.Count == 1, "Repeated protection changed faction again or duplicated the hediff.");
            foreach (string missing in new[] { "definition", "torso" })
            {
                Reset(); protection = missing == "definition" ? null : def; hasTorso = missing != "torso";
                RaidDefenderLootProtectionUtility.ApplyProxyPawnProtection(pawn, defender);
                Check(factionChanges == 1 && !pawn.guest.Recruitable && added == 0, "Missing definition/body part prevented faction protection or added an invalid hediff.");
            }
            Reset(); pawn.guest = null;
            RaidDefenderLootProtectionUtility.ApplyProxyPawnProtection(pawn, defender);
            Check(added == 1, "Pawn without a guest tracker lost health protection.");
            pawn.health = null;
            RaidDefenderLootProtectionUtility.ApplyProxyPawnProtection(pawn, defender);
            Check(added == 1 && factionChanges == 1, "Pawn without health duplicated protection or faction changes.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            faction = null; protection = null; torso = null!;
        }
    }

    private static bool Dead(ref bool __result) { __result = dead; return false; }
    private static bool Destroyed(ref bool __result) { __result = destroyed; return false; }
    private static bool FactionValue(ref Faction? __result) { __result = faction; return false; }
    private static bool Recruitable(ref bool __result) { __result = recruitable; return false; }
    private static bool SetRecruitable(bool __0) { recruitable = __0; return false; }
    private static bool Protection(ref HediffDef? __result) { __result = protection; return false; }
    private static bool SetFaction(Faction __0) { faction = __0; factionChanges++; return false; }
    private static bool BodyPart(ref BodyPartRecord __1, ref bool __result) { __1 = torso; __result = hasTorso; return false; }
    private static bool MakeHediff(HediffDef __0, Pawn __1, BodyPartRecord __2, ref Hediff __result)
    {
        Check(ReferenceEquals(__0, protection) && ReferenceEquals(__2, torso), "Protection used the wrong definition or body part.");
        __result = new Hediff { def = __0, pawn = __1 }; return false;
    }
    private static bool AddHediff(Pawn_HealthTracker __instance, Hediff __0, BodyPartRecord __1)
    {
        Check(ReferenceEquals(__1, torso), "Protection was applied to the wrong body part.");
        __instance.hediffSet.hediffs.Add(__0); added++; return false;
    }
    private static T New<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
