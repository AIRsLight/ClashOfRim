using System;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Diplomacy;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(FactionIdeosTracker), nameof(FactionIdeosTracker.ExposeData))]
public static class FactionIdeosLoadPatches
{
    private const string ServerMercenaryFactionDefName = "ClashOfRim_ServerMercenaries";
    private static readonly FieldInfo? FactionField = AccessTools.Field(typeof(FactionIdeosTracker), "faction");

    public static bool Prefix(FactionIdeosTracker __instance)
    {
        if (Scribe.mode != LoadSaveMode.PostLoadInit
            || !IdeologyPawnReferenceCompatibility.HasPawnReference
            || Find.IdeoManager is null
            || FactionField is null
            || __instance.PrimaryIdeo is not null)
        {
            return true;
        }

        if (FactionField.GetValue(__instance) is not Faction faction)
        {
            return true;
        }

        if (string.Equals(faction.def?.defName, ServerMercenaryFactionDefName, StringComparison.Ordinal))
        {
            RestoreManagedServerFactionIdeo(__instance, faction);
            return true;
        }

        if (!string.Equals(faction.def?.defName, PlayerFactionProxyUtility.ProxyFactionDefName, StringComparison.Ordinal))
        {
            return true;
        }

        string ownerUserId = PlayerFactionProxyUtility.ProxyOwnerUserId(faction);
        if (RemoteIdeoCatalog.TryFindPrimaryIdeoForOwner(ownerUserId, out Ideo? ownerIdeo) && ownerIdeo is not null)
        {
            __instance.SetPrimary(ownerIdeo);
            ClashLog.Message("[ClashOfRim][Ideo] Restored proxy faction ideo from owner catalog before vanilla post-load fallback: user="
                + ownerUserId
                + ", ideo="
                + ownerIdeo.name);
            return true;
        }

        Log.Error("[ClashOfRim][Ideo] Proxy faction owner ideo was unavailable; refusing vanilla random ideo fallback: user="
            + ownerUserId);
        return false;
    }

    private static void RestoreManagedServerFactionIdeo(FactionIdeosTracker tracker, Faction faction)
    {
        Ideo? stableFallback = Find.IdeoManager.IdeosListForReading
            .FirstOrDefault(ideo => ideo is not null && !ideo.initialPlayerIdeo)
            ?? Find.IdeoManager.IdeosListForReading.FirstOrDefault();
        if (stableFallback is null)
        {
            return;
        }

        tracker.SetPrimary(stableFallback);
        Log.Warning("[ClashOfRim][Ideo] Managed server faction had no ideo in the loaded save; restored a stable local ideo before vanilla random fallback: faction="
            + faction.def?.defName
            + ", fallback="
            + stableFallback.name);
    }
}
