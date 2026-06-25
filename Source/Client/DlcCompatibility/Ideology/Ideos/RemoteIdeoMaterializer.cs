using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

public static partial class RemoteIdeoMaterializer
{
    public static int ApplyServerCatalog(IEnumerable<ModWorldIdeoSummaryDto> serverIdeos, string? localUserId)
    {
        if (!IdeologyPawnReferenceCompatibility.HasPawnReference || Find.IdeoManager is null)
        {
            return 0;
        }

        int applied = 0;
        foreach (ModWorldIdeoSummaryDto dto in serverIdeos)
        {
            if (string.IsNullOrWhiteSpace(dto.GlobalKey))
            {
                continue;
            }

            try
            {
                bool localOwner = string.Equals(dto.OwnerUserId, localUserId, StringComparison.Ordinal);
                if (localOwner && TryRegisterLocal(dto))
                {
                    applied++;
                }
                else if (!localOwner && TryGetOrCreate(dto, out _))
                {
                    applied++;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[ClashOfRim] Skipped remote shadow ideo that could not be created {dto.GlobalKey}: {ex}");
            }
        }

        if (applied > 0)
        {
            Find.IdeoManager.SortIdeos();
        }

        return applied;
    }

    private static bool TryRegisterLocal(ModWorldIdeoSummaryDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.LocalId) || !int.TryParse(dto.LocalId, out int localId))
        {
            return false;
        }

        Ideo? localIdeo = Find.IdeoManager?.IdeosListForReading?
            .FirstOrDefault(ideo => ideo?.id == localId);
        if (localIdeo is null)
        {
            return false;
        }

        RemoteIdeoCatalog.Register(localIdeo, dto.GlobalKey, BuildDisplayMetadata(dto));
        return true;
    }

    public static bool TryGetOrCreate(ModWorldIdeoSummaryDto dto, out Ideo? ideo)
    {
        ideo = null;
        if (Find.IdeoManager is null || string.IsNullOrWhiteSpace(dto.GlobalKey))
        {
            return false;
        }

        if (RemoteIdeoCatalog.TryGetCatalogIdeo(dto.GlobalKey, out ideo))
        {
            if (RemoteIdeoCatalog.IsInCurrentIdeoManager(ideo))
            {
                ApplyShadowFields(ideo!, dto);
                RemoteIdeoCatalog.Register(ideo!, dto.GlobalKey, BuildDisplayMetadata(dto));
                RemoveDuplicateUnreferencedShadows(dto, ideo!);
                return true;
            }

            ClashLog.Message("[ClashOfRim][Ideo] Recreating stale remote ideo catalog entry: " + dto.GlobalKey);
            RemoteIdeoCatalog.Unregister(ideo!);
            ideo = null;
        }

        if (TryFindExistingShadow(dto, out ideo) && ideo is not null)
        {
            ApplyShadowFields(ideo, dto);
            RemoteIdeoCatalog.Register(ideo, dto.GlobalKey, BuildDisplayMetadata(dto));
            RemoveDuplicateUnreferencedShadows(dto, ideo);
            return true;
        }

        ideo = TryCreateIdeo(dto);
        if (ideo is null)
        {
            return false;
        }

        if (!Find.IdeoManager.Add(ideo))
        {
            ideo = null;
            return false;
        }

        RemoteIdeoCatalog.Register(ideo, dto.GlobalKey, BuildDisplayMetadata(dto));
        RemoveDuplicateUnreferencedShadows(dto, ideo);
        return true;
    }

    private static bool TryFindExistingShadow(ModWorldIdeoSummaryDto dto, out Ideo? ideo)
    {
        ideo = null;
        List<Ideo>? ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null)
        {
            return false;
        }

        ideo = ideos
            .Where(candidate => candidate is not null)
            .Where(candidate => !candidate.initialPlayerIdeo)
            .Where(candidate => !RemoteIdeoCatalog.TryGetGlobalKey(candidate, out _))
            .Where(candidate => MatchesShadowIdentity(candidate, dto))
            .OrderByDescending(IsPrimaryIdeoOfAnyFaction)
            .ThenBy(candidate => candidate.id)
            .FirstOrDefault();
        return ideo is not null;
    }

    private static void RemoveDuplicateUnreferencedShadows(ModWorldIdeoSummaryDto dto, Ideo keep)
    {
        List<Ideo>? ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null)
        {
            return;
        }

        foreach (Ideo duplicate in ideos
            .Where(candidate => candidate is not null && candidate != keep)
            .Where(candidate => !candidate.initialPlayerIdeo)
            .Where(candidate => MatchesShadowIdentity(candidate, dto))
            .Where(candidate => !IsReferenced(candidate))
            .ToList())
        {
            RemoteIdeoCatalog.Unregister(duplicate);
            Find.IdeoManager?.Remove(duplicate);
            ClashLog.Message("[ClashOfRim][Ideo] Removed duplicate remote ideo shadow: "
                + dto.GlobalKey
                + ", ideo="
                + duplicate.name);
        }
    }

    private static bool MatchesShadowIdentity(Ideo ideo, ModWorldIdeoSummaryDto dto)
    {
        if (ideo is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(dto.Name)
            && !string.Equals(ideo.name, dto.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(dto.Culture)
            && !string.Equals(ideo.culture?.defName, dto.Culture, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsPrimaryIdeoOfAnyFaction(Ideo ideo)
    {
        return Find.FactionManager?.AllFactionsListForReading?
            .Any(faction => faction?.ideos?.PrimaryIdeo == ideo) == true;
    }

    private static bool IsReferenced(Ideo ideo)
    {
        if (IsPrimaryIdeoOfAnyFaction(ideo))
        {
            return true;
        }

        if (Find.FactionManager?.AllFactionsListForReading?
            .Any(faction => faction?.ideos?.AllIdeos?.Contains(ideo) == true) == true)
        {
            return true;
        }

        return PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead
            .Any(pawn => pawn?.ideo?.Ideo == ideo);
    }

    private static void ApplyShadowFields(Ideo ideo, ModWorldIdeoSummaryDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.Name))
        {
            ideo.name = dto.Name;
        }
        else
        {
            ideo.name = dto.GlobalKey;
        }

        ideo.memberName = FactionDefOf.PlayerColony.basicMemberKind.label;

        CultureDef? culture = ResolveDef<CultureDef>(dto.Culture);
        if (culture is not null)
        {
            ideo.culture = culture;
        }

        IdeoIconDef? iconDef = ResolveDef<IdeoIconDef>(dto.IconDefName);
        ColorDef? colorDef = ResolveDef<ColorDef>(dto.ColorDefName);
        if (iconDef is not null && colorDef is not null)
        {
            ideo.SetIcon(iconDef, colorDef);
        }
        else if (!string.IsNullOrWhiteSpace(dto.IconDefName)
            || !string.IsNullOrWhiteSpace(dto.ColorDefName))
        {
            ClashLog.Message("[ClashOfRim][Ideo] Kept existing remote ideo icon because icon/color def could not be resolved: "
                + dto.GlobalKey);
        }

        if (!string.IsNullOrWhiteSpace(dto.PrimaryFactionColorHex)
            && ColorUtility.TryParseHtmlString(dto.PrimaryFactionColorHex, out Color primaryFactionColor))
        {
            ideo.primaryFactionColor = primaryFactionColor;
        }

        ideo.hidden = false;
        ideo.solid = true;
        ideo.initialPlayerIdeo = false;
    }

    private static RemoteIdeoDisplayMetadata BuildDisplayMetadata(ModWorldIdeoSummaryDto dto)
    {
        string? cultureLabel = dto.CultureLabel;
        string? cultureIconPath = dto.CultureIconPath;
        CultureDef? culture = ResolveDef<CultureDef>(dto.Culture);
        if (culture is not null)
        {
            cultureLabel ??= culture.LabelCap.ToString();
            cultureIconPath ??= culture.iconPath;
        }

        string? iconPath = dto.IconPath;
        IdeoIconDef? iconDef = ResolveDef<IdeoIconDef>(dto.IconDefName);
        iconPath ??= iconDef?.iconPath;

        string? colorHex = dto.ColorHex;
        ColorDef? colorDef = ResolveDef<ColorDef>(dto.ColorDefName);
        if (colorDef is not null && string.IsNullOrWhiteSpace(colorHex))
        {
            colorHex = "#" + ColorUtility.ToHtmlStringRGBA(colorDef.color);
        }

        return new RemoteIdeoDisplayMetadata(
            dto.GlobalKey,
            dto.OwnerUserId,
            dto.OwnerColonyId,
            dto.Name,
            dto.Culture,
            cultureLabel,
            cultureIconPath,
            dto.IconDefName,
            iconPath,
            dto.ColorDefName,
            colorHex,
            dto.PrimaryFactionColorHex,
            dto.InitialPlayerIdeo);
    }

    private static TDef? ResolveDef<TDef>(string? defName)
        where TDef : Def
    {
        return string.IsNullOrWhiteSpace(defName)
            ? null
            : DefDatabase<TDef>.GetNamedSilentFail(defName);
    }

}
