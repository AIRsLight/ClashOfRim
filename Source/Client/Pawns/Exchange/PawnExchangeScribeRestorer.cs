using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnExchangeScribeRestorer
{
    private static ClientSessionLifecycle? session;
    internal static void Attach(ClientSessionLifecycle? value) => session = value;
    private const string RelationshipPlaceholderTagPrefix = "ClashOfRim.RelationshipPlaceholder:";
    private const string PawnGlobalIdTagPrefix = "ClashOfRim.PawnGlobalId:";

    public static void StripLocalExchangeTags(Pawn? pawn)
    {
        if (pawn?.questTags is null)
        {
            return;
        }

        pawn.questTags.RemoveAll(tag => IsExchangeTag(tag));
    }

    public static int CleanupRelationshipArtifactsForRemovedPawn(Pawn? pawn)
    {
        if (pawn?.relations?.DirectRelations is null)
        {
            return 0;
        }

        List<Pawn> candidates = pawn.relations.DirectRelations
            .Where(relation => relation?.otherPawn is not null)
            .Select(relation => relation.otherPawn)
            .Where(IsRelationshipPlaceholder)
            .Distinct()
            .ToList();
        foreach (Pawn placeholder in candidates)
        {
            RemoveAllDirectRelationsBetween(pawn, placeholder);
        }

        int discarded = 0;
        foreach (Pawn placeholder in candidates)
        {
            if (!IsReferencedByAnyKnownPawn(placeholder))
            {
                DiscardWorldPawn(placeholder);
                discarded++;
            }
        }

        return discarded;
    }

    public static bool TryRestore(
        ModPawnExchangePackageDto package,
        Func<Pawn, bool> validator,
        string validatorFailure,
        string label,
        out Pawn? pawn,
        out string message,
        bool forcePlayerFaction = true)
    {
        pawn = null;
        message = string.Empty;
        if (!PawnScribeLoader.TryPrepare(package.Scribe, label, out var document, out message)) return false;

        if (!ClashOfRimCompatibilityApi.TryRegisterPawnExchangePackageExtensions(package, out string extensionMessage))
        {
            message = ClashOfRimText.Key(
                "ClashOfRim.PawnExchange.StatusExtensionFailed",
                label.Named("LABEL"),
                extensionMessage.Named("MESSAGE"));
            return false;
        }

        try
        {
            if (!PawnScribeLoader.TryLoad(document, label, out var restored, out message)) return false;

            if (!validator(restored))
            {
                message = validatorFailure;
                return false;
            }

            if (forcePlayerFaction && restored.Faction != Faction.OfPlayer)
            {
                restored.SetFaction(Faction.OfPlayer);
            }

            if (!PawnScribeLoader.ValidateRuntimeState(restored, label, out message))
            {
                return false;
            }

            PawnExchangeLoadIdUtility.LocalizeRestoredPawn(restored);
            ClashLog.Message("[ClashOfRim][PawnExchange] localized restored pawn label=" + label + " pawn=" + restored.ThingID + ".");
            string metadataMessage = ClashOfRimCompatibilityApi.RestorePawnReferenceMetadata(
                restored,
                package.Reference?.Metadata,
                label);
            if ((package.Reference?.Metadata is null || package.Reference.Metadata.Count == 0))
            {
                ClashLog.Message("[ClashOfRim][PawnExchange] Restored "
                    + label
                    + " has no pawn reference metadata; DLC references may fall back.");
            }
            else if (string.IsNullOrWhiteSpace(metadataMessage))
            {
                ClashLog.Message("[ClashOfRim][PawnExchange] No compatibility metadata was applied for restored "
                    + label
                    + "; metadataKeys="
                    + string.Join(",", package.Reference.Metadata.Keys));
            }
            MarkPawnGlobalId(restored, package.Reference?.GlobalId);
            int mergedPlaceholders = MergeRelationshipPlaceholdersForGlobalId(restored, package.Reference?.GlobalId);
            int relationCount = RestoreOneLayerRelationships(restored, package.Relationships);
            pawn = restored;
            string relationMessage = relationCount > 0
                ? ClashOfRimText.Key(
                    "ClashOfRim.PawnExchange.StatusRestoredWithRelations",
                    relationCount.ToString(CultureInfo.InvariantCulture).Named("COUNT"))
                : string.Empty;
            if (mergedPlaceholders > 0)
            {
                ClashLog.Message("[ClashOfRim][PawnExchange] Merged relationship placeholders into restored pawn "
                    + restored.LabelShort
                    + ": count="
                    + mergedPlaceholders
                    + ".");
            }

            string details = string.Concat(metadataMessage, relationMessage);
            message = ClashOfRimText.Key(
                "ClashOfRim.PawnExchange.StatusRestored",
                label.Named("LABEL"),
                details.Named("DETAILS"));
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or XmlException
            or InvalidOperationException
            or ArgumentException
            or NullReferenceException)
        {
            Log.Warning(
                "[ClashOfRim][PawnExchange] scribe restore failed label="
                + label
                + " global="
                + (package.Reference?.GlobalId ?? "<null>")
                + " thingDef="
                + (package.Identity?.ThingDef ?? "<null>")
                + ": "
                + ex);
            message = ClashOfRimText.Key(
                "ClashOfRim.PawnExchange.StatusRestoreFailed",
                label.Named("LABEL"),
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            return false;
        }
    }

    private static int RestoreOneLayerRelationships(
        Pawn pawn,
        IReadOnlyList<ModPawnExchangeRelationshipStubDto>? relationships)
    {
        if (pawn.relations is null || relationships is null || relationships.Count == 0)
        {
            return 0;
        }

        int restored = 0;
        int reused = 0;
        int created = 0;
        foreach (ModPawnExchangeRelationshipStubDto relationship in relationships.Take(128))
        {
            if (string.IsNullOrWhiteSpace(relationship.OtherPawnGlobalId)
                || string.IsNullOrWhiteSpace(relationship.RelationDef))
            {
                continue;
            }

            PawnRelationDef? relationDef = DefDatabase<PawnRelationDef>.GetNamedSilentFail(relationship.RelationDef);
            if (relationDef is null)
            {
                continue;
            }

            Pawn otherPawn = FindOrCreateRelationshipPawn(relationship, out bool createdPlaceholder);
            if (createdPlaceholder)
            {
                created++;
            }
            else
            {
                reused++;
            }

            if (otherPawn == pawn || pawn.relations.DirectRelationExists(relationDef, otherPawn))
            {
                continue;
            }

            pawn.relations.AddDirectRelation(relationDef, otherPawn);
            restored++;
        }

        if (restored > 0 || reused > 0 || created > 0)
        {
            ClashLog.Message("[ClashOfRim][PawnExchange] Restored relationships for pawn "
                + pawn.LabelShort
                + ": restored="
                + restored
                + ", reused="
                + reused
                + ", placeholders="
                + created
                + ".");
        }

        return restored;
    }

    private static Pawn FindOrCreateRelationshipPawn(
        ModPawnExchangeRelationshipStubDto relationship,
        out bool createdPlaceholder)
    {
        Pawn? existing = FindExistingPawnByGlobalId(relationship.OtherPawnGlobalId);
        if (existing is not null)
        {
            createdPlaceholder = false;
            RestoreRelationshipDeath(existing, relationship.OtherPawnDead, Find.WorldPawns);
            return existing;
        }

        createdPlaceholder = true;
        string tag = RelationshipPlaceholderTagPrefix + relationship.OtherPawnGlobalId.Trim();
        PawnKindDef kind = ResolveRelationshipPawnKind(relationship);
        Pawn placeholder = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer,
            forceGenerateNewPawn: true, canGeneratePawnRelations: false));
        if (!string.IsNullOrWhiteSpace(relationship.OtherPawnName))
        {
            placeholder.Name = new NameSingle(relationship.OtherPawnName!.Trim());
        }

        QuestUtility.AddQuestTag(ref placeholder.questTags, tag);
        MarkPawnGlobalId(placeholder, relationship.OtherPawnGlobalId);
        RestoreRelationshipDeath(placeholder, relationship.OtherPawnDead, Find.WorldPawns);
        if (Find.WorldPawns is not null && !Find.WorldPawns.Contains(placeholder))
        {
            Find.WorldPawns.PassToWorld(placeholder, PawnDiscardDecideMode.KeepForever);
        }

        return placeholder;
    }

    internal static void RestoreRelationshipDeath(Pawn pawn, bool isDead, WorldPawns? worldPawns)
    {
        // A relationship stub describes an absent pawn, not a new local death event.
        // Never kill a real local pawn or resurrect a placeholder from an older payload.
        if (isDead && IsRelationshipPlaceholder(pawn) && !pawn.Dead)
        {
            pawn.health.SetDead();
            worldPawns?.Notify_PawnDestroyed(pawn);
        }
    }

    internal static PawnKindDef ResolveRelationshipPawnKind(ModPawnExchangeRelationshipStubDto relationship)
    {
        InvalidOperationException Unresolved() =>
            new("Cannot resolve relationship pawn species: " + relationship.OtherPawnGlobalId);
        PawnKindDef? kind = string.IsNullOrWhiteSpace(relationship.OtherPawnKindDef) ? null
            : DefDatabase<PawnKindDef>.GetNamedSilentFail(relationship.OtherPawnKindDef);
        ThingDef? race = string.IsNullOrWhiteSpace(relationship.OtherPawnThingDef) ? null
            : DefDatabase<ThingDef>.GetNamedSilentFail(relationship.OtherPawnThingDef);
        // An explicit race is authoritative, including when its mod is unavailable.
        if (!string.IsNullOrWhiteSpace(relationship.OtherPawnThingDef) && race?.race is null)
            throw Unresolved();
        // Older payloads only carry a ThingID. Recover a loaded race from its exact
        // def-name prefix plus numeric ID; prefer the longest prefix for modded names.
        if (race is null && string.IsNullOrWhiteSpace(relationship.OtherPawnThingDef))
        {
            string id = ExtractGlobalPawnLocalThingId(relationship.OtherPawnGlobalId) ?? string.Empty;
            if (id.StartsWith("Thing_", StringComparison.Ordinal)) id = id.Substring(6);
            race = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.race is not null && id.StartsWith(d.defName, StringComparison.Ordinal)
                    && id.Length > d.defName.Length && id.Substring(d.defName.Length).All(char.IsDigit))
                .OrderByDescending(d => d.defName.Length).FirstOrDefault();
        }
        if (kind?.race?.race is not null && (race is null || kind.race == race)) return kind;
        if (race is not null)
        {
            kind = DefDatabase<PawnKindDef>.AllDefsListForReading.FirstOrDefault(k => k.race == race);
            if (kind is not null) return kind;
        }
        if (race is not null || !string.IsNullOrWhiteSpace(relationship.OtherPawnThingDef)
            || !string.IsNullOrWhiteSpace(relationship.OtherPawnKindDef)
            || relationship.RelationDef == "Bond")
            throw Unresolved();
        return PawnKindDefOf.Colonist;
    }

    private static Pawn? FindExistingPawnByGlobalId(string globalId)
    {
        if (string.IsNullOrWhiteSpace(globalId))
        {
            return null;
        }

        globalId = globalId.Trim();
        string globalTag = PawnGlobalIdTagPrefix + globalId;
        string placeholderTag = RelationshipPlaceholderTagPrefix + globalId;
        Pawn? tagged = EnumerateKnownPawns()
            .FirstOrDefault(pawn => pawn.questTags is not null
                && (pawn.questTags.Contains(globalTag, StringComparer.Ordinal)
                    || pawn.questTags.Contains(placeholderTag, StringComparer.Ordinal)));
        if (tagged is not null)
        {
            return tagged;
        }

        if (!PawnGlobalIdUtility.CanResolveLocalThingId(globalId, session?.ReadContext().UserId))
            return null;

        string? localThingId = ExtractGlobalPawnLocalThingId(globalId);
        if (string.IsNullOrWhiteSpace(localThingId))
        {
            return null;
        }

        return EnumerateKnownPawns()
            .FirstOrDefault(pawn => string.Equals(pawn.ThingID, localThingId, StringComparison.Ordinal)
                || string.Equals(pawn.GetUniqueLoadID(), localThingId, StringComparison.Ordinal)
                || string.Equals(pawn.GetUniqueLoadID(), "Thing_" + localThingId, StringComparison.Ordinal));
    }

    private static int MergeRelationshipPlaceholdersForGlobalId(Pawn restored, string? globalId)
    {
        if (restored is null || string.IsNullOrWhiteSpace(globalId))
        {
            return 0;
        }

        string placeholderTag = RelationshipPlaceholderTagPrefix + globalId!.Trim();
        List<Pawn> placeholders = EnumerateKnownPawns()
            .Where(pawn => pawn != restored
                && pawn.questTags is not null
                && pawn.questTags.Contains(placeholderTag, StringComparer.Ordinal))
            .Distinct()
            .ToList();
        int merged = 0;
        foreach (Pawn placeholder in placeholders)
        {
            MoveRelationsFromPlaceholder(restored, placeholder);
            if (!IsReferencedByAnyKnownPawn(placeholder))
            {
                DiscardWorldPawn(placeholder);
            }

            merged++;
        }

        return merged;
    }

    private static void MoveRelationsFromPlaceholder(Pawn restored, Pawn placeholder)
    {
        if (restored is null || placeholder is null || restored == placeholder)
        {
            return;
        }

        foreach (Pawn owner in EnumerateKnownPawns().Where(pawn => pawn != placeholder).ToList())
        {
            if (owner.relations?.DirectRelations is null)
            {
                continue;
            }

            foreach (DirectPawnRelation relation in owner.relations.DirectRelations
                         .Where(relation => relation?.otherPawn == placeholder && relation.def is not null)
                         .ToList())
            {
                AddDirectRelationIfMissing(owner, relation.def, restored);
                owner.relations.RemoveDirectRelation(relation.def, placeholder);
            }
        }

        if (placeholder.relations?.DirectRelations is null)
        {
            return;
        }

        foreach (DirectPawnRelation relation in placeholder.relations.DirectRelations
                     .Where(relation => relation?.otherPawn is not null && relation.def is not null)
                     .ToList())
        {
            if (relation.otherPawn != restored)
            {
                AddDirectRelationIfMissing(restored, relation.def, relation.otherPawn);
            }

            placeholder.relations.RemoveDirectRelation(relation.def, relation.otherPawn);
        }
    }

    private static void AddDirectRelationIfMissing(Pawn pawn, PawnRelationDef relationDef, Pawn otherPawn)
    {
        if (pawn == otherPawn || pawn.relations is null)
        {
            return;
        }

        if (!pawn.relations.DirectRelationExists(relationDef, otherPawn))
        {
            pawn.relations.AddDirectRelation(relationDef, otherPawn);
        }
    }

    private static IEnumerable<Pawn> EnumerateKnownPawns()
    {
        var seen = new HashSet<Pawn>();
        foreach (Map map in Find.Maps ?? new List<Map>())
        {
            foreach (Pawn pawn in map.mapPawns?.AllPawns ?? new List<Pawn>())
            {
                if (pawn is not null && seen.Add(pawn))
                {
                    yield return pawn;
                }
            }
        }

        if (Find.WorldObjects is not null)
        {
            foreach (Caravan caravan in Find.WorldObjects.Caravans)
            {
                foreach (Pawn pawn in caravan.PawnsListForReading ?? new List<Pawn>())
                {
                    if (pawn is not null && seen.Add(pawn))
                    {
                        yield return pawn;
                    }
                }
            }
        }

        if (Find.WorldPawns is not null)
        {
            foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead)
            {
                if (pawn is not null && seen.Add(pawn))
                {
                    yield return pawn;
                }
            }
        }
    }

    private static void MarkPawnGlobalId(Pawn pawn, string? globalId)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(globalId))
        {
            return;
        }

        QuestUtility.AddQuestTag(ref pawn.questTags, PawnGlobalIdTagPrefix + globalId!.Trim());
    }

    private static bool IsRelationshipPlaceholder(Pawn? pawn)
    {
        return pawn?.questTags is not null
            && pawn.questTags.Any(tag => tag.StartsWith(RelationshipPlaceholderTagPrefix, StringComparison.Ordinal));
    }

    private static bool IsExchangeTag(string? tag)
    {
        return !string.IsNullOrWhiteSpace(tag)
            && (tag!.StartsWith(RelationshipPlaceholderTagPrefix, StringComparison.Ordinal)
                || tag.StartsWith(PawnGlobalIdTagPrefix, StringComparison.Ordinal));
    }

    private static void RemoveAllDirectRelationsBetween(Pawn pawn, Pawn otherPawn)
    {
        if (pawn.relations?.DirectRelations is not null)
        {
            foreach (DirectPawnRelation relation in pawn.relations.DirectRelations
                         .Where(relation => relation?.otherPawn == otherPawn && relation.def is not null)
                         .ToList())
            {
                pawn.relations.RemoveDirectRelation(relation.def, otherPawn);
            }
        }

        if (otherPawn.relations?.DirectRelations is not null)
        {
            foreach (DirectPawnRelation relation in otherPawn.relations.DirectRelations
                         .Where(relation => relation?.otherPawn == pawn && relation.def is not null)
                         .ToList())
            {
                otherPawn.relations.RemoveDirectRelation(relation.def, pawn);
            }
        }
    }

    private static bool IsReferencedByAnyKnownPawn(Pawn placeholder)
    {
        foreach (Pawn pawn in EnumerateKnownPawns())
        {
            if (pawn == placeholder || pawn.relations?.DirectRelations is null)
            {
                continue;
            }

            if (pawn.relations.DirectRelations.Any(relation => relation?.otherPawn == placeholder))
            {
                return true;
            }
        }

        return false;
    }

    private static void DiscardWorldPawn(Pawn pawn)
    {
        if (pawn.Destroyed || pawn.Discarded)
        {
            return;
        }

        if (pawn.Spawned)
        {
            pawn.DeSpawnOrDeselect(DestroyMode.Vanish);
        }

        if (Find.WorldPawns is not null)
        {
            if (Find.WorldPawns.Contains(pawn))
            {
                Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
            }
            else
            {
                Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Discard);
            }

            return;
        }

        pawn.Destroy(DestroyMode.Vanish);
    }

    private static string? ExtractGlobalPawnLocalThingId(string globalId)
    {
        const string marker = "/pawn:";
        int start = globalId.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        int end = globalId.IndexOf('/', start);
        string value = end < 0
            ? globalId.Substring(start)
            : globalId.Substring(start, end - start);
        if (value.StartsWith("Thing_", StringComparison.Ordinal))
        {
            value = value.Substring("Thing_".Length);
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

}
