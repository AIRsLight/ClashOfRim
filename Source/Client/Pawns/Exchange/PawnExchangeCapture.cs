using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

// Captures game data only. Callers own admission, removal, upload and recovery.
internal sealed class PawnExchangeCapture
{
    private readonly string userId, colonyId;
    private readonly string? snapshotId;

    internal PawnExchangeCapture(string userId, string colonyId, string? snapshotId)
    { this.userId = userId; this.colonyId = colonyId; this.snapshotId = snapshotId; }

    internal ModPawnExchangePackageDto Capture(Pawn pawn, string globalKey,
        string referenceScope = "", IReadOnlyDictionary<string, string?>? preservedMetadata = null)
    {
        // A returning pawn may have had its exchange tags removed for departure.
        // Its own identity comes from the command/assignment, including on coded equipment.
        string GlobalId(Pawn referenced) => ReferenceEquals(referenced, pawn) ? globalKey : PawnGlobalIdUtility.Build(userId, referenced);
        var package = new ModPawnExchangePackageDto
        {
            PackageVersion = 1,
            Reference = BuildReference(pawn, globalKey, preservedMetadata),
            Identity = new ModPawnExchangeIdentityDto
            {
                ThingDef = pawn.def?.defName, PawnKindDef = pawn.kindDef?.defName,
                FactionDef = pawn.Faction?.def?.defName, Gender = pawn.gender.ToString()
            },
            Appearance = new ModPawnExchangeAppearanceDto
            {
                DisplayName = pawn.LabelShort, BodyTypeDef = pawn.story?.bodyType?.defName,
                HeadTypeDef = pawn.story?.headType?.defName, HairDef = pawn.story?.hairDef?.defName,
                BeardDef = pawn.style?.beardDef?.defName, SkinColor = null, HairColor = null
            },
            Status = new ModPawnExchangeStatusDto
            {
                Dead = pawn.Dead, BiologicalAgeTicks = pawn.ageTracker?.AgeBiologicalTicks,
                ChronologicalAgeTicks = pawn.ageTracker?.AgeChronologicalTicks,
                DeathCauseDef = null, HealthState = pawn.health?.State.ToString()
            },
            Apparel = pawn.apparel?.WornApparel.Select(item => Equipment(item, globalKey, "apparel", GlobalId)).ToList()
                ?? new List<ModPawnExchangeEquipmentItemDto>(),
            Equipment = pawn.equipment?.AllEquipmentListForReading.Select(item => Equipment(item, globalKey, "equipment", GlobalId)).ToList()
                ?? new List<ModPawnExchangeEquipmentItemDto>(),
            Relationships = Relationships(pawn, GlobalId),
            Scribe = ScribePayload(pawn, GlobalId, referenceScope)
        };
        ClashOfRimCompatibilityApi.AppendPawnExchangeExtensions(pawn, package);
        return package;
    }

    private ModCrossMapPawnReferenceDto BuildReference(Pawn pawn, string globalKey,
        IReadOnlyDictionary<string, string?>? preservedMetadata = null)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal);
        ClashOfRimCompatibilityApi.CollectPawnReferenceMetadata(pawn, metadata, userId, colonyId);
        if (preservedMetadata is not null)
            foreach (var entry in preservedMetadata)
                if (!string.IsNullOrWhiteSpace(entry.Key)) metadata[entry.Key] = entry.Value;
        return new ModCrossMapPawnReferenceDto
        {
            GlobalId = globalKey, SourceSnapshotId = snapshotId, Name = pawn.LabelShort,
            Dead = pawn.Dead, Faction = pawn.Faction?.def?.defName, Metadata = metadata
        };
    }

    private static ModPawnExchangeEquipmentItemDto Equipment(Thing thing, string globalKey, string container, Func<Pawn, string> globalId)
    {
        CompBiocodable? coding = thing.TryGetComp<CompBiocodable>();
        bool biocoded = coding?.Biocoded == true;
        Pawn? codedPawn = coding?.CodedPawn;
        return new ModPawnExchangeEquipmentItemDto
        {
            GlobalId = $"{globalKey}/{container}:{thing.ThingID}", Def = thing.def?.defName, Label = thing.LabelCapNoCount,
            StackCount = Math.Max(1, thing.stackCount),
            Quality = QualityUtility.TryGetQuality(thing, out QualityCategory quality) ? quality.ToString() : null,
            HitPoints = thing.def?.useHitPoints == true ? thing.HitPoints : null,
            WornByCorpse = (thing as Apparel)?.WornByCorpse,
            Biocoded = biocoded ? true : null,
            BiocodedPawnGlobalId = biocoded && codedPawn is not null ? globalId(codedPawn) : null,
            UniqueWeapon = TradeThingReferenceUtility.IsWeaponWithTraits(thing) ? true : null,
            UniqueWeaponName = null, UniqueWeaponTraits = TradeThingReferenceUtility.WeaponTraitDefNames(thing)
        };
    }

    private static List<ModPawnExchangeRelationshipStubDto> Relationships(Pawn pawn, Func<Pawn, string> globalId) =>
        pawn.relations?.DirectRelations
            .Where(relation => relation?.otherPawn is not null && !string.IsNullOrWhiteSpace(relation.otherPawn.ThingID))
            .Take(128)
            .Select(relation => new ModPawnExchangeRelationshipStubDto
            {
                OtherPawnGlobalId = globalId(relation.otherPawn), OtherPawnName = relation.otherPawn.LabelShort,
                OtherPawnDead = relation.otherPawn.Dead,
                OtherPawnThingDef = relation.otherPawn.def.defName,
                OtherPawnKindDef = relation.otherPawn.kindDef?.defName,
                RelationDef = relation.def?.defName
            }).ToList() ?? new List<ModPawnExchangeRelationshipStubDto>();

    private ModPawnScribePayloadDto? ScribePayload(Pawn pawn, Func<Pawn, string> globalId, string scope)
    {
        try
        {
            string? xml = Scribe.saver?.DebugOutputFor(pawn);
            if (string.IsNullOrWhiteSpace(xml))
            {
                Log.Warning("[ClashOfRim] Pawn Scribe output was empty; package will use structured fields only.");
                return null;
            }
            return new ModPawnScribePayloadDto
            {
                Xml = xml, XmlSha256 = Hash(xml), PawnReferenceReplacements = References(pawn, globalId, scope)
            };
        }
        // The previous reflected call wrapped every serialization exception in TargetInvocationException.
        // Keep capture failure local while removing that reflective engine API lookup.
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to build pawn Scribe payload: " + ex);
            return null;
        }
    }

    private List<ModPawnScribePawnReferenceReplacementDto> References(Pawn pawn, Func<Pawn, string> globalId, string scope)
    {
        var replacements = new Dictionary<string, ModPawnScribePawnReferenceReplacementDto>(StringComparer.Ordinal);
        void Add(Pawn? referenced)
        {
            if (referenced is null || ReferenceEquals(referenced, pawn)) return;
            string sourceId = referenced.GetUniqueLoadID();
            if (string.IsNullOrWhiteSpace(sourceId) || replacements.ContainsKey(sourceId)) return;
            string key = globalId(referenced);
            replacements.Add(sourceId, new ModPawnScribePawnReferenceReplacementDto
            {
                SourceLoadId = sourceId,
                PlaceholderLoadId = "ClashOfRimPlaceholderPawn_" + Hash(scope + key).Substring(0, 16),
                Reference = BuildReference(referenced, key)
            });
        }
        if (pawn.relations?.DirectRelations is not null)
            foreach (DirectPawnRelation relation in pawn.relations.DirectRelations) Add(relation?.otherPawn);
        if (pawn.apparel?.WornApparel is not null)
            foreach (Apparel item in pawn.apparel.WornApparel) Add(item.TryGetComp<CompBiocodable>()?.CodedPawn);
        if (pawn.equipment?.AllEquipmentListForReading is not null)
            foreach (ThingWithComps item in pawn.equipment.AllEquipmentListForReading) Add(item.TryGetComp<CompBiocodable>()?.CodedPawn);
        return replacements.Values.Take(128).ToList();
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
