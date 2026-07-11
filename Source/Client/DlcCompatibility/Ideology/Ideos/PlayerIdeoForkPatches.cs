using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class PlayerIdeoForkPolicy
{
    internal static bool ShouldFork(
        bool hasServerGlobalKey,
        bool referencedByNpcFaction,
        bool referencedByPlayerProxy)
    {
        return hasServerGlobalKey || (referencedByNpcFaction && !referencedByPlayerProxy);
    }
}

[HarmonyPatch(typeof(Scenario), nameof(Scenario.PostIdeoChosen))]
internal static class PlayerIdeoForkPatches
{
    private static readonly FieldInfo? PreceptIdField = typeof(Precept).GetField(
        "ID",
        BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Prefix()
    {
        if (!ClashOfRimCompatibilityApi.IsActiveMultiplayerSession)
        {
            return;
        }

        TryForkAdoptedNpcIdeo();
    }

    private static void TryForkAdoptedNpcIdeo()
    {
        Faction? playerFaction = Faction.OfPlayer;
        Ideo? source = playerFaction?.ideos?.PrimaryIdeo;
        if (playerFaction is null
            || source is null
            || Find.IdeoManager is null
            || source.foundation?.def is null)
        {
            return;
        }

        bool hasServerGlobalKey = RemoteIdeoCatalog.GetGlobalKeys(source)
            .Any(key => RemoteIdeoCatalog.TryGetDisplayMetadata(key, out RemoteIdeoDisplayMetadata? metadata)
                && string.Equals(metadata?.OwnerUserId, "server", StringComparison.OrdinalIgnoreCase));
        List<Faction> otherFactions = Find.FactionManager?.AllFactionsListForReading?
            .Where(faction => faction is not null && faction != Faction.OfPlayer)
            .Where(faction => faction.ideos?.AllIdeos?.Contains(source) == true)
            .ToList() ?? new List<Faction>();
        bool referencedByPlayerProxy = otherFactions.Any(PlayerFactionProxyUtility.IsServerPlayerProxy);
        bool referencedByNpcFaction = otherFactions.Any(faction => !PlayerFactionProxyUtility.IsServerPlayerProxy(faction));
        if (!PlayerIdeoForkPolicy.ShouldFork(
                hasServerGlobalKey,
                referencedByNpcFaction,
                referencedByPlayerProxy))
        {
            return;
        }

        if (PreceptIdField is null)
        {
            Log.Error("[ClashOfRim][Ideo] Cannot fork the adopted NPC ideology because Precept.ID was not found.");
            return;
        }

        Ideo fork = IdeoGenerator.MakeIdeo(source.foundation.def);
        source.CopyTo(fork);
        fork.classicMode = source.classicMode;
        fork.hidden = false;
        fork.solid = source.solid;
        fork.initialPlayerIdeo = true;
        try
        {
            foreach (Precept precept in fork.PreceptsListForReading)
            {
                PreceptIdField.SetValue(precept, Find.UniqueIDsManager.GetNextPreceptID());
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or FieldAccessException
            or TargetException)
        {
            Log.Error("[ClashOfRim][Ideo] Failed to assign fresh precept IDs to the adopted NPC ideology fork: "
                + exception);
            return;
        }

        if (!Find.IdeoManager.Add(fork))
        {
            Log.Error("[ClashOfRim][Ideo] Failed to add the player-owned NPC ideology fork.");
            return;
        }

        source.initialPlayerIdeo = false;
        playerFaction.ideos.SetPrimary(fork);
        IdeoUIUtility.SetSelected(fork);
        ClashLog.Message("[ClashOfRim][Ideo] Forked adopted NPC ideology for the player: source="
            + source.GetUniqueLoadID()
            + ":"
            + source.name
            + ", fork="
            + fork.GetUniqueLoadID()
            + ":"
            + fork.name);
    }
}
