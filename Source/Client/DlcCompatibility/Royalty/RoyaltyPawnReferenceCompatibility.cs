using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class RoyaltyPawnReferenceCompatibility
{
    private const char RoyaltyRecordSeparator = ';';
    private const char RoyaltyFieldSeparator = '|';
    private const string RoyaltyPawnExchangeExtensionState = "royaltyState";
    internal const string PawnMetadataRoyaltyState = "pawn.metadata.royaltyState";

    internal static bool HasRemoteStateGuards =>
        ClashOfRimCompatibilityApi.HasCompatibilityCapability(RoyaltyCompatibilityKeys.RemoteStateGuards);

    internal static void CollectRoyaltyPawnReferenceMetadata(Pawn pawn, Dictionary<string, string?> metadata, string? userId, string? colonyId)
    {
        string? value = ResolveRoyaltyPawnReferenceMetadata(
            pawn,
            PawnMetadataRoyaltyState,
            userId,
            colonyId);
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[PawnMetadataRoyaltyState] = value;
        }
    }

    internal static string? ResolveRoyaltyPawnReferenceMetadata(Pawn pawn, string metadataKey, string? userId, string? colonyId)
    {
        if (!string.Equals(metadataKey, PawnMetadataRoyaltyState, StringComparison.Ordinal)
            || !HasRemoteStateGuards
            || pawn?.royalty is null)
        {
            return null;
        }

        var records = new Dictionary<string, RoyaltyStateRecord>(StringComparer.Ordinal);
        foreach (RoyalTitle title in pawn.royalty.AllTitlesForReading ?? new List<RoyalTitle>())
        {
            if (title?.faction?.def is null || title.def is null)
            {
                continue;
            }

            string factionDefName = title.faction.def.defName;
            records[factionDefName] = new RoyaltyStateRecord(
                factionDefName,
                title.def.defName,
                pawn.royalty.GetFavor(title.faction));
        }

        foreach (KeyValuePair<Faction, int> favor in EnumerateRoyalFavor(pawn.royalty))
        {
            if (favor.Key?.def is null || favor.Value == 0)
            {
                continue;
            }

            string factionDefName = favor.Key.def.defName;
            records[factionDefName] = records.TryGetValue(factionDefName, out RoyaltyStateRecord existing)
                ? new RoyaltyStateRecord(existing.FactionDefName, existing.TitleDefName, favor.Value)
                : new RoyaltyStateRecord(factionDefName, null, favor.Value);
        }

        if (records.Count == 0)
        {
            return null;
        }

        return string.Join(
            RoyaltyRecordSeparator.ToString(),
            records.Values.Select(record =>
                EscapeRoyaltyField(record.FactionDefName)
                + RoyaltyFieldSeparator
                + EscapeRoyaltyField(record.TitleDefName)
                + RoyaltyFieldSeparator
                + record.Favor.ToString(CultureInfo.InvariantCulture)));
    }

    internal static void AppendRoyaltyPawnExchangeExtension(Pawn pawn, ModPawnExchangePackageDto package)
    {
        if (!HasRemoteStateGuards || pawn is null || package is null)
        {
            return;
        }

        string? state = ResolveRoyaltyPawnReferenceMetadata(
            pawn,
            PawnMetadataRoyaltyState,
            userId: null,
            colonyId: null);
        if (string.IsNullOrWhiteSpace(state))
        {
            return;
        }

        package.Extensions.Add(new ModPawnExchangeExtensionPackageDto
        {
            ProviderId = RoyaltyCompatibilityKeys.PackageId,
            Kind = RoyaltyCompatibilityKeys.RemoteStateGuards,
            Metadata = new Dictionary<string, string?>
            {
                [RoyaltyPawnExchangeExtensionState] = state
            }
        });
    }

    internal static string? ResolveRoyaltyPawnExchangeExtensionMetadata(
        ModPawnExchangePackageDto package,
        string metadataKey)
    {
        if (!HasRemoteStateGuards
            || package?.Extensions is null
            || !string.Equals(metadataKey, PawnMetadataRoyaltyState, StringComparison.Ordinal))
        {
            return null;
        }

        ModPawnExchangeExtensionPackageDto? extension = package.Extensions.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, RoyaltyCompatibilityKeys.PackageId, StringComparison.Ordinal)
            && string.Equals(extension.Kind, RoyaltyCompatibilityKeys.RemoteStateGuards, StringComparison.Ordinal));
        return extension?.Metadata is not null
            && extension.Metadata.TryGetValue(RoyaltyPawnExchangeExtensionState, out string? value)
            ? value
            : null;
    }

    internal static string RestoreRoyaltyPawnReferenceMetadata(Pawn pawn, string metadataKey, string? metadataValue, string label)
    {
        if (!string.Equals(metadataKey, PawnMetadataRoyaltyState, StringComparison.Ordinal)
            || !HasRemoteStateGuards
            || pawn is null
            || string.IsNullOrWhiteSpace(metadataValue))
        {
            return string.Empty;
        }

        if (pawn.royalty is null)
        {
            pawn.royalty = new Pawn_RoyaltyTracker(pawn);
        }

        int restored = 0;
        foreach (RoyaltyStateRecord record in ParseRoyaltyState(metadataValue!))
        {
            Faction? faction = ResolveRoyaltyFaction(record.FactionDefName);
            if (faction is null)
            {
                continue;
            }

            RoyalTitleDef? title = string.IsNullOrWhiteSpace(record.TitleDefName)
                ? null
                : DefDatabase<RoyalTitleDef>.GetNamedSilentFail(record.TitleDefName);
            if (!string.IsNullOrWhiteSpace(record.TitleDefName) && title is null)
            {
                continue;
            }

            pawn.royalty.SetTitle(faction, title, grantRewards: false, rewardsOnlyForNewestTitle: false, sendLetter: false);
            pawn.royalty.SetFavor(faction, Math.Max(0, record.Favor), notifyOnFavorChanged: false);
            restored++;
        }

        return restored == 0
            ? string.Empty
            : ClashOfRimText.Key(
                "ClashOfRim.PawnExchange.StatusRoyaltyRestored",
                restored.ToString(CultureInfo.InvariantCulture).Named("COUNT"));
    }

    private static IEnumerable<KeyValuePair<Faction, int>> EnumerateRoyalFavor(Pawn_RoyaltyTracker tracker)
    {
        try
        {
            FieldInfo? favorField = typeof(Pawn_RoyaltyTracker).GetField(
                "favor",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return favorField?.GetValue(tracker) is Dictionary<Faction, int> favor
                ? favor.ToList()
                : Enumerable.Empty<KeyValuePair<Faction, int>>();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            Log.Warning("[ClashOfRim][Royalty] Failed to read pawn royal favor state: " + ex);
            return Enumerable.Empty<KeyValuePair<Faction, int>>();
        }
    }

    private static Faction? ResolveRoyaltyFaction(string? factionDefName)
    {
        if (string.IsNullOrWhiteSpace(factionDefName))
        {
            return null;
        }

        return Find.FactionManager?.AllFactionsListForReading?
            .FirstOrDefault(faction => string.Equals(faction?.def?.defName, factionDefName, StringComparison.Ordinal));
    }

    internal static void SanitizeRoyaltyRemoteMapProjection(
        ModSnapshotPackageMetadataDto package,
        XElement mapElement,
        XElement referencePawnsElement)
    {
        if (!HasRemoteStateGuards)
        {
            return;
        }

        ClearPawnRoyaltyRuntimeState(mapElement);
        ClearPawnRoyaltyRuntimeState(referencePawnsElement);
    }

    private static int ClearPawnRoyaltyRuntimeState(XElement mapElement)
    {
        int cleared = 0;
        foreach (XElement pawn in mapElement
                     .Descendants("thing")
                     .Concat(mapElement.Descendants("li"))
                     .Where(IsProjectionPawnElement)
                     .ToList())
        {
            foreach (XElement royalty in pawn
                         .Descendants("royalty")
                         .Where(element => !IsNullElement(element))
                         .ToList())
            {
                ReplaceOrAdd(royalty, "titles", new XElement("titles"));
                ReplaceOrAdd(royalty, "favor", EmptyDictionary("favor"));
                ReplaceOrAdd(royalty, "highestTitles", EmptyDictionary("highestTitles"));
                ReplaceOrAdd(royalty, "heirs", EmptyDictionary("heirs"));
                ReplaceOrAdd(royalty, "permits", new XElement("permits"));
                ReplaceOrAdd(royalty, "abilities", new XElement("abilities"));
                ReplaceOrAdd(royalty, "lastDecreeTicks", new XElement("lastDecreeTicks", "-999999"));
                cleared++;
            }
        }

        if (cleared > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection][Royalty] Cleared remote pawn royalty state: "
                + cleared
                + ".");
        }

        return cleared;
    }

    private static XElement EmptyDictionary(string name)
    {
        return new XElement(name, new XElement("keys"), new XElement("values"));
    }

    private static void ReplaceOrAdd(XElement parent, string name, XElement replacement)
    {
        XElement? existing = parent.Element(name);
        if (existing is null)
        {
            parent.Add(replacement);
        }
        else
        {
            existing.ReplaceWith(replacement);
        }
    }

    private static bool IsProjectionPawnElement(XElement element)
    {
        return element.Element("kindDef") is not null
            && element.Element("pather") is not null
            && element.Element("jobs") is not null;
    }

    private static bool IsNullElement(XElement element)
    {
        return string.Equals(element.Attribute("IsNull")?.Value, "True", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<RoyaltyStateRecord> ParseRoyaltyState(string value)
    {
        foreach (string record in value.Split(new[] { RoyaltyRecordSeparator }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = record.Split(new[] { RoyaltyFieldSeparator }, 3);
            if (fields.Length < 3)
            {
                continue;
            }

            if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int favor))
            {
                favor = 0;
            }

            yield return new RoyaltyStateRecord(
                UnescapeRoyaltyField(fields[0]),
                UnescapeRoyaltyField(fields[1]),
                favor);
        }
    }

    private static string EscapeRoyaltyField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value!.Length);
        foreach (char ch in value!)
        {
            if (ch is RoyaltyRecordSeparator or RoyaltyFieldSeparator or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string UnescapeRoyaltyField(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool escaped = false;
        foreach (char ch in value)
        {
            if (escaped)
            {
                builder.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private sealed class RoyaltyStateRecord
    {
        public RoyaltyStateRecord(string factionDefName, string? titleDefName, int favor)
        {
            FactionDefName = factionDefName;
            TitleDefName = titleDefName;
            Favor = favor;
        }

        public string FactionDefName { get; }

        public string? TitleDefName { get; }

        public int Favor { get; }
    }
}
