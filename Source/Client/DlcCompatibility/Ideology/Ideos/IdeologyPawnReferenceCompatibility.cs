using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class IdeologyPawnReferenceCompatibility
{
    private const string IdeologyPawnExchangeExtensionIdeoGlobalId = "ideoGlobalId";
    private const string IdeologyPawnExchangeExtensionIdeoName = "ideoName";
    private const string IdeologyPawnExchangeExtensionIdeoIconDef = "ideoIconDef";
    private const string IdeologyPawnExchangeExtensionIdeoColorDef = "ideoColorDef";
    internal const string PawnMetadataIdeoGlobalId = "pawn.metadata.ideoGlobalId";

    internal static bool HasPawnReference =>
        ClashOfRimCompatibilityApi.HasCompatibilityCapability(IdeologyCompatibilityKeys.PawnReference);

    internal static void CollectPawnReferenceMetadata(Pawn pawn, Dictionary<string, string?> metadata, string? userId, string? colonyId)
    {
        string? value = ResolvePawnReferenceMetadata(
            pawn,
            PawnMetadataIdeoGlobalId,
            userId,
            colonyId);
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[PawnMetadataIdeoGlobalId] = value;
        }
    }

    internal static string? ResolvePawnReferenceMetadata(Pawn pawn, string metadataKey, string? userId, string? colonyId)
    {
        if (!string.Equals(metadataKey, PawnMetadataIdeoGlobalId, StringComparison.Ordinal)
            || !HasPawnReference
            || pawn?.Ideo is null)
        {
            return null;
        }

        if (RemoteIdeoCatalog.TryGetGlobalKeyForOwner(pawn.Ideo, userId, out string? registeredKey)
            && !string.IsNullOrWhiteSpace(registeredKey))
        {
            return registeredKey;
        }

        if (RemoteIdeoCatalog.TryGetGlobalKey(pawn.Ideo, out registeredKey)
            && !string.IsNullOrWhiteSpace(registeredKey))
        {
            return registeredKey;
        }

        string localId = pawn.Ideo.id.ToString(CultureInfo.InvariantCulture);
        return BuildLocalIdeoGlobalKey(userId, localId);
    }

    internal static string RestorePawnReferenceMetadata(Pawn pawn, string metadataKey, string? metadataValue, string label)
    {
        if (!string.Equals(metadataKey, PawnMetadataIdeoGlobalId, StringComparison.Ordinal)
            || !HasPawnReference
            || pawn is null
            || string.IsNullOrWhiteSpace(metadataValue))
        {
            return string.Empty;
        }

        if ((!RemoteIdeoCatalog.TryGetIdeo(metadataValue!, out Ideo? ideo) || ideo is null)
            && TryParseIdeoGlobalKey(metadataValue, out string? ownerUserId, out string? localId))
        {
            RemoteIdeoCatalog.TryFindIdeoByLocalIdForOwner(localId!, ownerUserId, out ideo, out _);
            if (ideo is null
                && RemoteIdeoCatalog.TryFindPrimaryIdeoForOwner(ownerUserId, out Ideo? primaryOwnerIdeo)
                && primaryOwnerIdeo is not null)
            {
                ideo = primaryOwnerIdeo;
                ClashLog.Message("[ClashOfRim][PawnExchange] Restored "
                    + label
                    + " ideo by owner primary fallback: missing="
                    + metadataValue
                    + ", owner="
                    + ownerUserId
                    + ", fallback="
                    + primaryOwnerIdeo.name);
            }
        }

        if (ideo is null)
        {
            Log.Warning("[ClashOfRim][PawnExchange] Could not restore " + label + " ideo because global ideo is missing: " + metadataValue);
            return ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusIdeoCatalogMissing");
        }

        if (!TrySetPawnIdeo(pawn, ideo))
        {
            Log.Warning("[ClashOfRim][PawnExchange] Could not assign restored " + label + " ideo: " + metadataValue);
            return ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusIdeoAssignFailed");
        }

        return ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusIdeoRestored");
    }

    internal static void AppendIdeologyPawnExchangeExtension(Pawn pawn, ModPawnExchangePackageDto package)
    {
        if (!HasPawnReference || pawn?.Ideo is null || package is null)
        {
            return;
        }

        string? globalId = ResolveIdeoGlobalIdFromPackage(package);
        if (string.IsNullOrWhiteSpace(globalId)
            && RemoteIdeoCatalog.TryGetGlobalKey(pawn.Ideo, out string? registeredKey))
        {
            globalId = registeredKey;
        }

        var metadata = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(globalId))
        {
            metadata[IdeologyPawnExchangeExtensionIdeoGlobalId] = globalId;
        }

        metadata[IdeologyPawnExchangeExtensionIdeoName] = pawn.Ideo.name;
        metadata[IdeologyPawnExchangeExtensionIdeoIconDef] = pawn.Ideo.iconDef?.defName;
        metadata[IdeologyPawnExchangeExtensionIdeoColorDef] = pawn.Ideo.colorDef?.defName;

        package.Extensions.Add(new ModPawnExchangeExtensionPackageDto
        {
            ProviderId = IdeologyCompatibilityKeys.PackageId,
            Kind = IdeologyCompatibilityKeys.PawnReference,
            Metadata = metadata
        });
    }

    internal static void NormalizeIdeologyPawnExchangePackage(ModPawnExchangePackageDto package)
    {
        if (!HasPawnReference || package is null)
        {
            return;
        }

        package.Reference ??= new ModCrossMapPawnReferenceDto();
        package.Reference.Metadata ??= new Dictionary<string, string?>();

        string? globalId = ResolveIdeoGlobalIdFromPackage(package);
        if (!string.IsNullOrWhiteSpace(globalId))
        {
            package.Reference.Metadata[PawnMetadataIdeoGlobalId] = globalId;
        }

        ModPawnExchangeExtensionPackageDto? extension = package.Extensions.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, IdeologyCompatibilityKeys.PackageId, StringComparison.Ordinal)
            && string.Equals(extension.Kind, IdeologyCompatibilityKeys.PawnReference, StringComparison.Ordinal));
        if (extension is null)
        {
            extension = new ModPawnExchangeExtensionPackageDto
            {
                ProviderId = IdeologyCompatibilityKeys.PackageId,
                Kind = IdeologyCompatibilityKeys.PawnReference,
                Metadata = new Dictionary<string, string?>()
            };
            package.Extensions.Add(extension);
        }

        extension.Metadata ??= new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(globalId))
        {
            extension.Metadata[IdeologyPawnExchangeExtensionIdeoGlobalId] = globalId;
        }

        if (extension.Metadata.Count == 0)
        {
            package.Extensions.Remove(extension);
        }
    }

    internal static bool IsIdeologyLocalSaveOnlyLoadId(string loadId)
    {
        return !string.IsNullOrWhiteSpace(loadId)
            && loadId.Trim().StartsWith("Ideo_", StringComparison.Ordinal);
    }

    private static string? ResolveIdeoGlobalIdFromPackage(ModPawnExchangePackageDto package)
    {
        if (package.Reference?.Metadata is not null
            && package.Reference.Metadata.TryGetValue(PawnMetadataIdeoGlobalId, out string? metadataValue)
            && !string.IsNullOrWhiteSpace(metadataValue))
        {
            return metadataValue;
        }

        ModPawnExchangeExtensionPackageDto? extension = package.Extensions?.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, IdeologyCompatibilityKeys.PackageId, StringComparison.Ordinal)
            && string.Equals(extension.Kind, IdeologyCompatibilityKeys.PawnReference, StringComparison.Ordinal));
        if (extension?.Metadata is not null
            && extension.Metadata.TryGetValue(IdeologyPawnExchangeExtensionIdeoGlobalId, out string? extensionValue)
            && !string.IsNullOrWhiteSpace(extensionValue))
        {
            return extensionValue;
        }

        return null;
    }

    private static bool TrySetPawnIdeo(Pawn pawn, Ideo ideo)
    {
        try
        {
            object? tracker = typeof(Pawn).GetField("ideo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(pawn);
            if (tracker is null)
            {
                return false;
            }

            MethodInfo? setIdeo = tracker.GetType().GetMethod(
                "SetIdeo",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Ideo) },
                modifiers: null);
            if (setIdeo is not null)
            {
                setIdeo.Invoke(tracker, new object[] { ideo });
                return pawn.Ideo == ideo;
            }

            FieldInfo? ideoField = tracker.GetType().GetField(
                "ideo",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ideoField is null)
            {
                return false;
            }

            ideoField.SetValue(tracker, ideo);
            return pawn.Ideo == ideo;
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            Log.Warning("[ClashOfRim][PawnExchange] Pawn ideo reflection assignment failed: " + ex);
            return false;
        }
    }

    internal static void ApplyPawnSoldEffects(Pawn pawn, Pawn? negotiator)
    {
        if (!HasPawnReference || pawn is null || !pawn.IsSlaveOfColony || negotiator is null)
        {
            return;
        }

        Find.HistoryEventsManager.RecordEvent(
            new HistoryEvent(HistoryEventDefOf.SoldSlave, negotiator.Named(HistoryEventArgsNames.Doer)),
            true);
    }

    internal static bool IsTradeableSlavePawn(Pawn pawn)
    {
        return HasPawnReference
            && pawn is { Destroyed: false, Dead: false }
            && pawn.IsSlaveOfColony;
    }

    internal static bool IsRestorableTradeSlavePawn(Pawn pawn)
    {
        return HasPawnReference
            && pawn is { Destroyed: false, Dead: false }
            && pawn.IsSlave;
    }

    internal static IReadOnlyList<ModWorldIdeoSummaryDto> ReadCurrentWorldIdeos(
        string? userId,
        string? colonyId,
        string worldConfigurationId)
    {
        List<ModWorldIdeoSummaryDto> ideos = new();
        if (!HasPawnReference || Find.IdeoManager?.IdeosListForReading is null)
        {
            return ideos;
        }

        foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading)
        {
            if (ideo is null)
            {
                continue;
            }

            string localId = ideo.id.ToString(CultureInfo.InvariantCulture);
            Faction? ownerFaction = FindFactionForIdeo(ideo);
            bool playerOwned = ideo.initialPlayerIdeo
                || ReferenceEquals(ownerFaction, Faction.OfPlayer)
                || ownerFaction?.IsPlayer == true;
            string ownerUserId = playerOwned ? userId ?? string.Empty : "server";
            string? ownerColonyId = playerOwned ? colonyId : null;
            string? savedIdeoPackageXml = TryExportIdeoPackageXml(ideo);
            ideos.Add(new ModWorldIdeoSummaryDto
            {
                GlobalKey = BuildLocalIdeoGlobalKey(ownerUserId, localId),
                OwnerUserId = ownerUserId,
                OwnerColonyId = ownerColonyId,
                SourceSnapshotId = worldConfigurationId,
                LocalId = localId,
                Name = ideo.name,
                Culture = ideo.culture?.defName,
                CultureLabel = ideo.culture?.LabelCap.ToString(),
                CultureIconPath = ideo.culture?.iconPath,
                PrimaryFactionColor = ideo.primaryFactionColor?.ToString(),
                PrimaryFactionColorHex = ideo.primaryFactionColor.HasValue
                    ? "#" + ColorUtility.ToHtmlStringRGBA(ideo.primaryFactionColor.Value)
                    : null,
                FoundationDefName = ideo.foundation?.def?.defName,
                FactionDefName = ownerFaction?.def?.defName,
                IconDefName = ideo.iconDef?.defName,
                IconPath = ideo.iconDef?.iconPath,
                ColorDefName = ideo.colorDef?.defName,
                ColorHex = ideo.colorDef is null ? null : "#" + ColorUtility.ToHtmlStringRGBA(ideo.colorDef.color),
                SavedIdeoPackageXml = savedIdeoPackageXml,
                SavedIdeoPackageSha256 = string.IsNullOrWhiteSpace(savedIdeoPackageXml)
                    ? null
                    : ComputeSha256Hex(savedIdeoPackageXml!),
                UpdatedAtGameTicks = Find.TickManager?.TicksGame,
                MemeDefNames = ideo.memes?
                    .Where(meme => meme != null)
                    .Select(meme => meme.defName)
                    .Where(defName => !string.IsNullOrWhiteSpace(defName))
                    .Distinct(StringComparer.Ordinal)
                    .ToList() ?? new List<string>(),
                PreceptDefNames = ideo.PreceptsListForReading?
                    .Where(precept => precept?.def != null)
                    .Select(precept => precept.def.defName)
                    .Where(defName => !string.IsNullOrWhiteSpace(defName))
                    .Distinct(StringComparer.Ordinal)
                    .ToList() ?? new List<string>(),
                StyleCategoryDefNames = ideo.thingStyleCategories?
                    .Where(style => style?.category != null)
                    .Select(style => style.category.defName)
                    .Where(defName => !string.IsNullOrWhiteSpace(defName))
                    .Distinct(StringComparer.Ordinal)
                    .ToList() ?? new List<string>(),
                Hidden = ideo.hidden,
                InitialPlayerIdeo = ideo.initialPlayerIdeo,
                MemeCount = ideo.memes?.Count ?? 0,
                PreceptCount = ideo.PreceptsListForReading?.Count ?? 0
            });
        }

        return ideos;
    }

    private static string BuildLocalIdeoGlobalKey(string? userId, string localId)
    {
        return $"owner:{Segment(userId)}/ideo:{localId}";
    }

    private static bool TryParseIdeoGlobalKey(string? globalKey, out string? ownerUserId, out string? localId)
    {
        ownerUserId = null;
        localId = null;
        if (string.IsNullOrWhiteSpace(globalKey))
        {
            return false;
        }

        string value = globalKey!.Trim();
        const string ownerPrefix = "owner:";
        const string colonyMarker = "/colony:";
        const string ideoMarker = "/ideo:";
        if (!value.StartsWith(ownerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int ideoIndex = value.LastIndexOf(ideoMarker, StringComparison.Ordinal);
        if (ideoIndex < 0)
        {
            return false;
        }

        string ownerSegment = value.Substring(ownerPrefix.Length, ideoIndex - ownerPrefix.Length);
        int colonyIndex = ownerSegment.IndexOf(colonyMarker, StringComparison.Ordinal);
        if (colonyIndex >= 0)
        {
            ownerSegment = ownerSegment.Substring(0, colonyIndex);
        }

        string parsedLocalId = value.Substring(ideoIndex + ideoMarker.Length).Trim();
        if (string.IsNullOrWhiteSpace(ownerSegment) || string.IsNullOrWhiteSpace(parsedLocalId))
        {
            return false;
        }

        ownerUserId = ownerSegment;
        localId = parsedLocalId.StartsWith("Ideo_", StringComparison.Ordinal)
            ? parsedLocalId.Substring("Ideo_".Length)
            : parsedLocalId;
        return !string.IsNullOrWhiteSpace(localId);
    }

    internal static bool TryComputeIdeoPackageSha256(Ideo ideo, out string? sha256)
    {
        sha256 = null;
        string? savedIdeoPackageXml = TryExportIdeoPackageXml(ideo);
        if (string.IsNullOrWhiteSpace(savedIdeoPackageXml))
        {
            return false;
        }

        sha256 = ComputeSha256Hex(savedIdeoPackageXml!);
        return true;
    }

    private static string? TryExportIdeoPackageXml(Ideo ideo)
    {
        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            return null;
        }

        string? path = null;
        try
        {
            string directory = Path.Combine(Path.GetTempPath(), "ClashOfRim", "Ideos");
            Directory.CreateDirectory(directory);
            path = Path.Combine(
                directory,
                "ideo-" + ideo.id.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".ideo");
            GameDataSaveLoader.SaveIdeo(ideo, path);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Warning("[ClashOfRim] Failed to export ideoligion package: " + ex);
            return null;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                TryDeleteFile(path!);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Faction? FindFactionForIdeo(Ideo ideo)
    {
        if (Find.World?.factionManager?.AllFactions is null)
        {
            return null;
        }

        return Find.World.factionManager.AllFactions
            .FirstOrDefault(candidate => candidate?.def != null && candidate.ideos?.Has(ideo) == true);
    }

    internal static void PreparePlayerProxyFaction(Faction faction, string ownerUserId)
    {
        if (!HasPawnReference || Find.IdeoManager is null || faction.def?.humanlikeFaction != true)
        {
            return;
        }

        faction.ideos ??= new FactionIdeosTracker(faction);
        if (RemoteIdeoCatalog.TryFindPrimaryIdeoForOwner(ownerUserId, out Ideo? ownerIdeo) && ownerIdeo is not null)
        {
            if (faction.ideos.PrimaryIdeo != ownerIdeo)
            {
                Ideo? previousPrimary = faction.ideos.PrimaryIdeo;
                faction.ideos.SetPrimary(ownerIdeo);
                RemoveUnreferencedProxyIdeo(previousPrimary, ownerIdeo);
                ClashLog.Message("[ClashOfRim][Ideo] Bound player proxy faction to owner ideo: user="
                    + ownerUserId
                    + ", ideo="
                    + ownerIdeo.name);
            }

            return;
        }

        if (faction.ideos.PrimaryIdeo is not null)
        {
            return;
        }

        ChooseOrGeneratePreparedFactionIdeo(faction, "PlayerProxy");
    }

    private static void RemoveUnreferencedProxyIdeo(Ideo? previousIdeo, Ideo ownerIdeo)
    {
        if (previousIdeo is null
            || previousIdeo == ownerIdeo
            || previousIdeo.initialPlayerIdeo
            || Find.IdeoManager?.IdeosListForReading?.Contains(previousIdeo) != true)
        {
            return;
        }

        if (Find.FactionManager?.AllFactionsListForReading?
            .Any(faction => faction?.ideos?.AllIdeos?.Contains(previousIdeo) == true) == true)
        {
            return;
        }

        if (PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead
            .Any(pawn => pawn?.ideo?.Ideo == previousIdeo))
        {
            return;
        }

        RemoteIdeoCatalog.Unregister(previousIdeo);
        Find.IdeoManager.Remove(previousIdeo);
        ClashLog.Message("[ClashOfRim][Ideo] Removed unreferenced proxy fallback ideo: " + previousIdeo.name);
    }

    internal static void PrepareFactionIdeology(Faction faction, string purpose, string? ownerUserId)
    {
        if (!HasPawnReference || Find.IdeoManager is null || faction.def?.humanlikeFaction != true)
        {
            return;
        }

        faction.ideos ??= new FactionIdeosTracker(faction);
        if (faction.ideos.PrimaryIdeo is not null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(ownerUserId)
            && RemoteIdeoCatalog.TryFindPrimaryIdeoForOwner(ownerUserId, out Ideo? ownerIdeo)
            && ownerIdeo is not null)
        {
            faction.ideos.SetPrimary(ownerIdeo);
            ClashLog.Message("[ClashOfRim][Ideo] Bound prepared faction to owner ideo: purpose="
                + Segment(purpose)
                + ", user="
                + ownerUserId
                + ", ideo="
                + ownerIdeo.name);
            return;
        }

        ChooseOrGeneratePreparedFactionIdeo(faction, purpose);
    }

    private static void ChooseOrGeneratePreparedFactionIdeo(Faction faction, string purpose)
    {
        if (Find.IdeoManager is null || faction.def is null)
        {
            return;
        }

        faction.ideos ??= new FactionIdeosTracker(faction);
        if (faction.ideos.PrimaryIdeo is not null)
        {
            return;
        }

        faction.ideos.ChooseOrGenerateIdeo(new IdeoGenerationParms(
            faction.def,
            false,
            null,
            null,
            faction.def.forcedMemes,
            false,
            false,
            false,
            false,
            faction.def.ideoName,
            faction.def.styles,
            faction.def.deityPresets,
            true,
            faction.def.ideoDescription,
            faction.def.requiredPreceptsOnly));

        if (faction.ideos.PrimaryIdeo is not null)
        {
            ClashLog.Message("[ClashOfRim][Ideo] Generated prepared faction ideo: purpose="
                + Segment(purpose)
                + ", faction="
                + faction.def.defName
                + ", ideo="
                + faction.ideos.PrimaryIdeo.name);
        }
    }

    private static string Segment(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value!.Trim();
    }

    private static string ComputeSha256Hex(string text)
    {
        using SHA256 sha256 = SHA256.Create();
        return ToHexLower(sha256.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    private static string ToHexLower(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

}
