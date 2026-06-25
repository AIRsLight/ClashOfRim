using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal static class TradeUiUtility
{
    public const float RowHeight = 34f;
    public const float IconSize = 28f;
    private static readonly IReadOnlyDictionary<string, int> DefaultFixedFeePerThing =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Wastepack"] = 100
        };
    private static readonly Dictionary<string, string> ConcreteThingLabelCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ThingLabelCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Thing> ConcreteThingIconCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Pawn> PawnPreviewCache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> FailedPawnPreviewCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<ThingDef, Pawn> PawnDefIconCache = new();
    private static readonly Dictionary<ThingDef, PawnKindDef?> PawnKindByRaceCache = new();
    private static readonly HashSet<ThingDef> FailedPawnDefIconCache = new();
    [ThreadStatic]
    private static int displayOnlyMetadataSuppressionDepth;

    internal static bool SuppressDisplayOnlyMetadataParts => displayOnlyMetadataSuppressionDepth > 0;

    public sealed class TradeThingDisplayGroup
    {
        public TradeThingDisplayGroup(ModThingReferenceDto representative, int count, bool isGrouped = false)
        {
            Representative = representative;
            Count = Math.Max(1, count);
            IsGrouped = isGrouped;
        }

        public ModThingReferenceDto Representative { get; }

        public int Count { get; }

        public bool IsGrouped { get; }
    }

    public static IReadOnlyList<TradeThingDisplayGroup> BuildDisplayGroups(IReadOnlyList<ModThingReferenceDto>? things)
    {
        if (things is null || things.Count == 0)
        {
            return Array.Empty<TradeThingDisplayGroup>();
        }

        var groups = new List<TradeThingDisplayGroup>();
        var groupedThings = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ModThingReferenceDto thing in things)
        {
            string? groupKey = DisplayGroupKey(thing);
            if (string.IsNullOrWhiteSpace(groupKey))
            {
                groups.Add(new TradeThingDisplayGroup(thing, 1));
                continue;
            }

            string key = groupKey!;
            int count = DisplayGroupCount(thing);
            if (groupedThings.TryGetValue(key, out int existingIndex))
            {
                TradeThingDisplayGroup existing = groups[existingIndex];
                groups[existingIndex] = new TradeThingDisplayGroup(
                    existing.Representative,
                    existing.Count + count,
                    isGrouped: true);
            }
            else
            {
                groupedThings[key] = groups.Count;
                groups.Add(new TradeThingDisplayGroup(thing, count));
            }
        }

        return groups;
    }

    public static IReadOnlyList<ModThingReferenceDto> BuildDisplayGroupRepresentatives(
        IReadOnlyList<ModThingReferenceDto>? things)
    {
        if (things is null || things.Count == 0)
        {
            return Array.Empty<ModThingReferenceDto>();
        }

        return BuildDisplayGroups(things)
            .Select(group => group.Representative)
            .ToList();
    }

    private static string? DisplayGroupKey(ModThingReferenceDto thing)
    {
        return PawnDisplayGroupKey(thing) ?? ThingDisplayGroupKey(thing);
    }

    private static int DisplayGroupCount(ModThingReferenceDto thing)
    {
        return TradePawnUtility.IsPawnReference(thing)
            ? 1
            : Math.Max(1, thing.StackCount);
    }

    private static string? PawnDisplayGroupKey(ModThingReferenceDto thing)
    {
        if (!TradePawnUtility.IsPawnReference(thing))
        {
            return null;
        }

        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        if (def?.race?.Humanlike == true)
        {
            return null;
        }

        string? defName = def?.defName
            ?? thing.DefName
            ?? thing.PawnPackage?.Identity?.ThingDef
            ?? PawnMetadataValue(thing, TradePawnUtility.PawnMetadataThingDef);
        if (string.IsNullOrWhiteSpace(defName))
        {
            return null;
        }

        var builder = new StringBuilder(160);
        AppendCacheKeyPart(builder, "pawn");
        AppendCacheKeyPart(builder, defName);
        AppendCacheKeyPart(builder, thing.PawnPackage?.Identity?.PawnKindDef
            ?? PawnMetadataValue(thing, TradePawnUtility.PawnMetadataPawnKindDef));
        AppendCacheKeyPart(builder, PawnGenderValue(thing, def));
        AppendCacheKeyPart(builder, PawnDisplayLifeStageBucket(def, PawnBiologicalAgeTicks(thing)));
        return builder.ToString();
    }

    private static string? ThingDisplayGroupKey(ModThingReferenceDto thing)
    {
        if (TradePawnUtility.IsPawnReference(thing))
        {
            return null;
        }

        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        string? defName = thing.MinifiedInnerDefName ?? thing.DefName ?? def?.defName;
        if (string.IsNullOrWhiteSpace(defName))
        {
            return null;
        }

        var builder = new StringBuilder(320);
        AppendCacheKeyPart(builder, "thing");
        AppendCacheKeyPart(builder, thing.DefName);
        AppendCacheKeyPart(builder, thing.MinifiedInnerDefName);
        AppendCacheKeyPart(builder, thing.MinifiedInnerStuffDefName ?? thing.StuffDefName);
        AppendCacheKeyPart(builder, thing.MinifiedInnerQuality ?? thing.Quality);
        AppendCacheKeyPart(builder, HitPointsDisplayBucket(thing));
        AppendCacheKeyPart(builder, thing.WornByCorpse?.ToString());
        AppendCacheKeyPart(builder, thing.Biocoded == true ? "biocoded" : string.Empty);
        AppendCacheKeyPart(builder, thing.UniqueWeapon?.ToString());
        AppendCacheKeyPart(builder, TradeThingReferenceUtility.WeaponTraitKind(thing));
        foreach (string trait in (thing.UniqueWeaponTraits ?? new List<string>())
                     .Where(trait => !string.IsNullOrWhiteSpace(trait))
                     .OrderBy(trait => trait, StringComparer.OrdinalIgnoreCase))
        {
            AppendCacheKeyPart(builder, trait);
        }

        foreach (KeyValuePair<string, string?> metadata in SemanticMetadataGroupParts(thing)
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AppendCacheKeyPart(builder, metadata.Key);
            AppendCacheKeyPart(builder, metadata.Value);
        }

        return builder.ToString();
    }

    private static string? HitPointsDisplayBucket(ModThingReferenceDto thing)
    {
        int? hitPoints = thing.MinifiedInnerHitPoints ?? thing.HitPoints;
        if (!hitPoints.HasValue)
        {
            return null;
        }

        int? maxHitPoints = thing.MinifiedInnerMaxHitPoints ?? thing.MaxHitPoints;
        if (!maxHitPoints.HasValue || maxHitPoints.Value <= 0)
        {
            return "raw:" + hitPoints.Value.ToString(CultureInfo.InvariantCulture);
        }

        int percent = HitPointsPercent(hitPoints.Value, maxHitPoints.Value);
        int bucket = Math.Max(0, Math.Min(9, (percent - 1) / 10));
        return "pct:" + bucket.ToString(CultureInfo.InvariantCulture);
    }

    private static string? HitPointsDisplayBucketLabel(ModThingReferenceDto thing)
    {
        int? hitPoints = thing.MinifiedInnerHitPoints ?? thing.HitPoints;
        int? maxHitPoints = thing.MinifiedInnerMaxHitPoints ?? thing.MaxHitPoints;
        if (!hitPoints.HasValue || !maxHitPoints.HasValue || maxHitPoints.Value <= 0)
        {
            return null;
        }

        int percent = HitPointsPercent(hitPoints.Value, maxHitPoints.Value);
        int bucket = Math.Max(0, Math.Min(9, (percent - 1) / 10));
        int lower = bucket * 10 + 1;
        int upper = (bucket + 1) * 10;
        return lower.ToString(CultureInfo.InvariantCulture)
            + "-"
            + upper.ToString(CultureInfo.InvariantCulture)
            + "%";
    }

    private static IEnumerable<KeyValuePair<string, string?>> SemanticMetadataGroupParts(ModThingReferenceDto thing)
    {
        if (thing.Metadata is null || thing.Metadata.Count == 0)
        {
            yield break;
        }

        foreach (KeyValuePair<string, string?> pair in thing.Metadata)
        {
            if (IsDisplayOnlyMetadata(pair.Key))
            {
                continue;
            }

            yield return pair;
        }
    }

    private static bool IsDisplayOnlyMetadata(string? metadataKey)
    {
        return string.Equals(metadataKey, "clashofrim.core.sourceLabels", StringComparison.Ordinal)
            || string.Equals(metadataKey, "clashofrim.core.artTitle", StringComparison.Ordinal)
            || string.Equals(metadataKey, "clashofrim.core.artAuthor", StringComparison.Ordinal)
            || string.Equals(metadataKey, "clashofrim.core.artDescription", StringComparison.Ordinal)
            || string.Equals(metadataKey, "clashofrim.thirdparty.compStatueStateXmlBase64", StringComparison.Ordinal)
            || string.Equals(metadataKey, TradeThingReferenceUtility.UniqueWeaponNameMetadataKey, StringComparison.Ordinal)
            || string.Equals(metadataKey, TradeThingReferenceUtility.UniqueWeaponColorDefMetadataKey, StringComparison.Ordinal);
    }

    private static string? PawnDisplayLifeStageBucket(ThingDef? def, long? biologicalAgeTicks)
    {
        LifeStageAge? lifeStage = PawnDisplayLifeStage(def, biologicalAgeTicks);
        string? lifeStageDefName = lifeStage?.def?.defName;
        if (!string.IsNullOrWhiteSpace(lifeStageDefName))
        {
            return lifeStageDefName;
        }

        if (!biologicalAgeTicks.HasValue || biologicalAgeTicks.Value < 0)
        {
            return null;
        }

        const long ticksPerYear = 3600000L;
        return (biologicalAgeTicks.Value / ticksPerYear).ToString(CultureInfo.InvariantCulture);
    }

    private static LifeStageAge? PawnDisplayLifeStage(ThingDef? def, long? biologicalAgeTicks)
    {
        if (!biologicalAgeTicks.HasValue
            || biologicalAgeTicks.Value < 0
            || def?.race?.lifeStageAges is not { Count: > 0 } lifeStageAges)
        {
            return null;
        }

        float ageYears = biologicalAgeTicks.Value / 3600000f;
        LifeStageAge? selected = null;
        foreach (LifeStageAge candidate in lifeStageAges)
        {
            if (ageYears < candidate.minAge)
            {
                break;
            }

            selected = candidate;
        }

        return selected ?? lifeStageAges[0];
    }

    public static void DrawThingIcon(Rect rect, string? defName)
    {
        ThingDef? def = ResolveThingDef(defName);
        if (def is null)
        {
            return;
        }

        if (def.category == ThingCategory.Pawn)
        {
            if (TryGetOrBuildPawnDefIcon(def, out Pawn? pawn) && pawn is not null)
            {
                Widgets.ThingIcon(rect, pawn, 1f, null, false, 1f, false);
                return;
            }

            DrawPawnPreviewUnavailable(rect);
            return;
        }

        Widgets.ThingIcon(rect, def, null, null, 1f, null, null, 1f);
    }

    public static bool DrawThingIconWithInfo(Rect rect, string? defName)
    {
        DrawThingIcon(rect, defName);
        ThingDef? def = ResolveThingDef(defName);
        if (def is not null)
        {
            return DrawInfoButtonHotspot(rect, () => OpenThingInfo(def));
        }

        return false;
    }

    public static void DrawThingIcon(Rect rect, ModThingReferenceDto thing)
    {
        bool isPawnReference = TradePawnUtility.IsPawnReference(thing);
        bool requiresConcretePawnPreview = RequiresConcretePawnPreview(thing);
        if (isPawnReference && TryGetOrBuildPawnPreview(thing, out Pawn? previewPawn) && previewPawn is not null)
        {
            try
            {
                Widgets.ThingIcon(rect, previewPawn, 1f, null, false, 1f, false);
                return;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Failed to draw cached pawn trade preview icon for "
                    + (thing.DisplayLabel ?? thing.DefName ?? "<unknown>")
                    + ": "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
            }

            DrawPawnPreviewUnavailable(rect);
            return;
        }

        if (requiresConcretePawnPreview)
        {
            DrawPawnPreviewUnavailable(rect);
            return;
        }

        if (!isPawnReference
            && TryGetConcreteIconThing(thing, out Thing? concreteThing)
            && concreteThing is not null)
        {
            try
            {
                Widgets.ThingIcon(rect, concreteThing, 1f, null, false, 1f, false);
                return;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Failed to draw concrete trade icon for "
                    + (thing.DefName ?? "<unknown>")
                    + ": "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
            }
        }

        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        if (def is null)
        {
            return;
        }

        try
        {
            Widgets.ThingIcon(rect, def, null, null, 1f, null, null, 1f);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to draw trade def icon for "
                + def.defName
                + ": "
                + ex.GetType().Name
                + " "
                + ex.Message);
        }
    }

    public static bool DrawThingIconWithInfo(Rect rect, ModThingReferenceDto thing)
    {
        DrawThingIcon(rect, thing);
        bool requiresConcretePawnPreview = RequiresConcretePawnPreview(thing);
        if (TradePawnUtility.IsPawnReference(thing)
            && TryGetOrBuildPawnPreview(thing, out Pawn? previewPawn)
            && previewPawn is not null)
        {
            return DrawInfoButtonHotspot(rect, () => OpenThingInfo(previewPawn));
        }

        if (requiresConcretePawnPreview)
        {
            return false;
        }

        if (!TradePawnUtility.IsPawnReference(thing)
            && TryGetConcreteIconThing(thing, out Thing? concreteThing)
            && concreteThing is not null)
        {
            return DrawInfoButtonHotspot(rect, () => OpenThingInfo(concreteThing));
        }

        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        if (def is not null)
        {
            return DrawInfoButtonHotspot(rect, () => OpenThingInfo(def));
        }

        return false;
    }

    public static bool DrawThingDisplayGroupIconWithInfo(Rect rect, TradeThingDisplayGroup group)
    {
        bool openedInfo = DrawThingIconWithInfo(rect, group.Representative);
        if (group.IsGrouped)
        {
            DrawCountBadge(rect, group.Count);
        }

        return openedInfo;
    }

    public static void PreparePawnPreviewsForTradeOrders(IReadOnlyList<ModTradeOrderSummaryDto>? orders)
    {
        if (orders is null || orders.Count == 0)
        {
            return;
        }

        foreach (ModTradeOrderSummaryDto order in orders)
        {
            PreparePawnPreviews(order.OfferedThings, order.EventId);
            PreparePawnPreviews(order.RequestedThings, order.EventId);
        }
    }

    public static void PreparePawnPreviewsForThingReferences(
        IReadOnlyList<ModThingReferenceDto>? things,
        string? sourceId = null)
    {
        PreparePawnPreviews(things, sourceId);
    }

    private static void PreparePawnPreviews(IReadOnlyList<ModThingReferenceDto>? things, string? eventId)
    {
        if (things is null || things.Count == 0)
        {
            return;
        }

        foreach (ModThingReferenceDto reference in BuildDisplayGroupRepresentatives(things))
        {
            PreparePawnPreview(reference, eventId);
        }
    }

    private static void PreparePawnPreview(ModThingReferenceDto reference, string? eventId)
    {
        if (!TradePawnUtility.IsPawnReference(reference) || reference.PawnPackage is null)
        {
            return;
        }

        string cacheKey = PawnPreviewCacheKey(reference);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        if (PawnPreviewCache.ContainsKey(cacheKey) || FailedPawnPreviewCache.Contains(cacheKey))
        {
            return;
        }

        if (!GiftPawnPackageUtility.TryRestoreTradePawn(reference.PawnPackage, out Pawn? pawn, out string message)
            || pawn is null)
        {
            FailedPawnPreviewCache.Add(cacheKey);
            Log.Warning("[ClashOfRim] Failed to restore trade pawn preview for order "
                + (eventId ?? "<unknown>")
                + " item "
                + (reference.DisplayLabel ?? reference.DefName ?? "<unknown>")
                + ": "
                + message);
            return;
        }

        if (!TryWarmPawnPreviewGraphics(pawn, reference, eventId))
        {
            FailedPawnPreviewCache.Add(cacheKey);
            return;
        }

        if (PawnPreviewCache.Count > 128)
        {
            PawnPreviewCache.Clear();
            FailedPawnPreviewCache.Clear();
        }

        PawnPreviewCache[cacheKey] = pawn;
    }

    private static bool TryWarmPawnPreviewGraphics(Pawn pawn, ModThingReferenceDto reference, string? eventId)
    {
        return TryWarmPawnPreviewGraphics(
            pawn,
            reference.DisplayLabel ?? reference.DefName ?? "<unknown>",
            eventId);
    }

    private static bool TryWarmPawnPreviewGraphics(Pawn pawn, string itemLabel, string? eventId)
    {
        try
        {
            PawnRenderer? renderer = pawn.Drawer?.renderer;
            if (renderer is null)
            {
                Log.Warning("[ClashOfRim] Restored trade pawn preview has no renderer for order "
                    + (eventId ?? "<unknown>")
                    + " item "
                    + itemLabel);
                return false;
            }

            renderer.SetAllGraphicsDirty();
            renderer.EnsureGraphicsInitialized();
            if (pawn.RaceProps?.Humanlike == true)
            {
                RenderTexture? portrait = PortraitsCache.Get(
                    pawn,
                    new Vector2(IconSize, IconSize),
                    Rot4.South,
                    default,
                    1f,
                    supersample: true,
                    compensateForUIScale: true,
                    renderHeadgear: true,
                    renderClothes: true);
                if (portrait is null)
                {
                    Log.Warning("[ClashOfRim] Restored trade pawn preview has no renderable portrait for order "
                        + (eventId ?? "<unknown>")
                        + " item "
                        + itemLabel);
                    return false;
                }

                return true;
            }

            Graphic? bodyGraphic = renderer.BodyGraphic;
            if (bodyGraphic?.MatAt(Rot4.East, null)?.mainTexture is null)
            {
                Log.Warning("[ClashOfRim] Restored trade pawn preview has no renderable body graphic for order "
                    + (eventId ?? "<unknown>")
                    + " item "
                    + itemLabel);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to initialize trade pawn preview graphics for order "
                + (eventId ?? "<unknown>")
                + " item "
                + itemLabel
                + ": "
                + ex.GetType().Name
                + " "
                + ex.Message);
            return false;
        }
    }

    private static void DrawPawnPreviewUnavailable(Rect rect)
    {
        Color previousColor = GUI.color;
        GUI.color = new Color(0.42f, 0.42f, 0.42f, 0.7f);
        Widgets.DrawBoxSolid(rect.ContractedBy(4f), GUI.color);
        GUI.color = previousColor;
    }

    private static bool TryGetOrBuildPawnPreview(ModThingReferenceDto reference, out Pawn? pawn)
    {
        pawn = null;
        string cacheKey = PawnPreviewCacheKey(reference);
        if (!TradePawnUtility.IsPawnReference(reference) || string.IsNullOrWhiteSpace(cacheKey))
        {
            return false;
        }

        if (PawnPreviewCache.TryGetValue(cacheKey, out pawn))
        {
            return true;
        }

        return false;
    }

    private static bool RequiresConcretePawnPreview(ModThingReferenceDto reference)
    {
        return false;
    }

    private static bool TryGetOrBuildPawnDefIcon(ThingDef raceDef, out Pawn? pawn)
    {
        pawn = null;
        if (raceDef.category != ThingCategory.Pawn)
        {
            return false;
        }

        if (PawnDefIconCache.TryGetValue(raceDef, out pawn))
        {
            return true;
        }

        if (FailedPawnDefIconCache.Contains(raceDef))
        {
            return false;
        }

        PawnKindDef? pawnKind = ResolvePawnKindForRace(raceDef);
        if (pawnKind is null)
        {
            FailedPawnDefIconCache.Add(raceDef);
            return false;
        }

        try
        {
            pawn = PawnGenerator.GeneratePawn(pawnKind, Faction.OfPlayer);
            if (!TryWarmPawnPreviewGraphics(pawn, raceDef.defName, eventId: null))
            {
                FailedPawnDefIconCache.Add(raceDef);
                pawn = null;
                return false;
            }

            if (PawnDefIconCache.Count > 64)
            {
                PawnDefIconCache.Clear();
                FailedPawnDefIconCache.Clear();
            }

            PawnDefIconCache[raceDef] = pawn;
            return true;
        }
        catch (Exception ex)
        {
            FailedPawnDefIconCache.Add(raceDef);
            Log.Warning("[ClashOfRim] Failed to build pawn def icon for "
                + raceDef.defName
                + ": "
                + ex.GetType().Name
                + " "
                + ex.Message);
            pawn = null;
            return false;
        }
    }

    private static PawnKindDef? ResolvePawnKindForRace(ThingDef raceDef)
    {
        if (PawnKindByRaceCache.TryGetValue(raceDef, out PawnKindDef? cached))
        {
            return cached;
        }

        PawnKindDef? pawnKind = DefDatabase<PawnKindDef>.AllDefsListForReading
            .FirstOrDefault(kind => kind.race == raceDef);
        PawnKindByRaceCache[raceDef] = pawnKind;
        return pawnKind;
    }

    private static string PawnPreviewCacheKey(ModThingReferenceDto reference)
    {
        if (!string.IsNullOrWhiteSpace(reference.PawnPackageId))
        {
            return "id:" + reference.PawnPackageId;
        }

        string? globalId = reference.PawnPackage?.Reference?.GlobalId;
        string? xmlHash = reference.PawnPackage?.Scribe?.XmlSha256;
        if (!string.IsNullOrWhiteSpace(globalId) || !string.IsNullOrWhiteSpace(xmlHash))
        {
            return "inline:" + (globalId ?? string.Empty) + "\u001f" + (xmlHash ?? string.Empty);
        }

        return string.IsNullOrWhiteSpace(reference.GlobalKey) ? string.Empty : "key:" + reference.GlobalKey;
    }

    private static bool TryGetConcreteIconThing(ModThingReferenceDto reference, out Thing? thing)
    {
        if (ShouldAvoidConcreteThingConstruction(reference))
        {
            thing = null;
            return false;
        }

        string cacheKey = ConcreteThingLabelCacheKey(reference);
        if (ConcreteThingIconCache.TryGetValue(cacheKey, out thing))
        {
            return true;
        }

        if (!TradeThingReferenceUtility.TryMakeThing(reference, stackCount: 1, out thing, out _)
            || thing is null)
        {
            return false;
        }

        if (ConcreteThingIconCache.Count > 512)
        {
            ConcreteThingIconCache.Clear();
        }

        ConcreteThingIconCache[cacheKey] = thing;
        return true;
    }

    private static void DrawCountBadge(Rect iconRect, int count)
    {
        if (count <= 1)
        {
            return;
        }

        string label = "x" + count.ToString(CultureInfo.InvariantCulture);
        Rect badgeRect = new(iconRect.xMax - 23f, iconRect.yMax - 13f, 25f, 14f);
        Color previousColor = GUI.color;
        GameFont previousFont = Text.Font;
        TextAnchor previousAnchor = Text.Anchor;
        GUI.color = new Color(0f, 0f, 0f, 0.76f);
        Widgets.DrawBoxSolid(badgeRect, GUI.color);
        GUI.color = Color.white;
        Text.Font = GameFont.Tiny;
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(badgeRect, label);
        Text.Anchor = previousAnchor;
        Text.Font = previousFont;
        GUI.color = previousColor;
    }

    public static void DrawThingIcon(Rect rect, Thing thing)
    {
        Widgets.ThingIcon(rect, thing, 1f, null, false, 1f, false);
    }

    public static bool DrawThingIconWithInfo(Rect rect, Thing thing)
    {
        DrawThingIcon(rect, thing);
        return DrawInfoButtonHotspot(rect, () => OpenThingInfo(thing));
    }

    private static bool DrawInfoButtonHotspot(Rect rect, Action openInfo)
    {
        if (Mouse.IsOver(rect))
        {
            Widgets.DrawHighlight(rect);
        }

        if (Widgets.ButtonInvisible(rect))
        {
            openInfo();
            return true;
        }

        return false;
    }

    private static void OpenThingInfo(Thing thing)
    {
        Find.WindowStack.Add(new Dialog_InfoCard(thing));
    }

    private static void OpenThingInfo(ThingDef def)
    {
        if (def.MadeFromStuff)
        {
            Find.WindowStack.Add(new Dialog_InfoCard(def, GenStuff.DefaultStuffFor(def)));
            return;
        }

        Find.WindowStack.Add(new Dialog_InfoCard(def));
    }

    public static void DrawTruncatedLabel(Rect rect, string? label, TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        string text = label ?? string.Empty;
        TextAnchor previousAnchor = Text.Anchor;
        bool previousWordWrap = Text.WordWrap;
        Text.Anchor = anchor;
        Text.WordWrap = false;
        string visibleText = text.Truncate(rect.width, null);
        Widgets.Label(rect, visibleText);
        Text.WordWrap = previousWordWrap;
        Text.Anchor = previousAnchor;

        if (!string.Equals(visibleText, text, StringComparison.Ordinal))
        {
            TooltipHandler.TipRegion(rect, text);
        }
    }

    public static ThingDef? ResolveThingDef(string? defName)
    {
        return string.IsNullOrWhiteSpace(defName)
            ? null
            : DefDatabase<ThingDef>.GetNamedSilentFail(defName);
    }

    public static string ThingLabel(
        ModThingReferenceDto thing,
        bool asRequirement = true,
        string? qualityRequirementMode = null,
        string? hitPointsRequirementMode = null,
        int? displayStackCount = null,
        string? displayHitPointsLabel = null)
    {
        string cacheKey = ThingLabelCacheKey(
            thing,
            asRequirement,
            qualityRequirementMode,
            hitPointsRequirementMode,
            displayStackCount,
            displayHitPointsLabel);
        if (ThingLabelCache.TryGetValue(cacheKey, out string cached))
        {
            return cached;
        }

        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        bool useConcreteRequirementLabel = asRequirement && ShouldUseConcreteRequirementLabel(thing);
        string label = !asRequirement || useConcreteRequirementLabel
            ? ConcreteThingLabel(thing, def)
            : def?.label?.CapitalizeFirst() ?? thing.DefName ?? ClashOfRimText.Key("ClashOfRim.UnknownItem");
        string suffix = FormatConditionSuffix(
            thing,
            asRequirement,
            useConcreteRequirementLabel,
            qualityRequirementMode,
            hitPointsRequirementMode,
            displayHitPointsLabel);
        if (def?.category == ThingCategory.Pawn
            || thing.PawnPackage is not null
            || !string.IsNullOrWhiteSpace(thing.PawnPackageId))
        {
            string pawnLabel = label + suffix;
            CacheThingLabel(cacheKey, pawnLabel);
            return pawnLabel;
        }

        int stackCount = Math.Max(1, displayStackCount ?? thing.StackCount);
        string thingLabel = $"{label} x{stackCount}{suffix}";
        CacheThingLabel(cacheKey, thingLabel);
        return thingLabel;
    }

    public static string ThingDisplayGroupLabel(
        TradeThingDisplayGroup group,
        bool asRequirement = true,
        string? qualityRequirementMode = null,
        string? hitPointsRequirementMode = null)
    {
        if (!group.IsGrouped)
        {
            return ThingLabel(group.Representative, asRequirement, qualityRequirementMode, hitPointsRequirementMode);
        }

        if (TradePawnUtility.IsPawnReference(group.Representative))
        {
            return PawnDisplayGroupLabel(group, asRequirement, qualityRequirementMode, hitPointsRequirementMode);
        }

        using (SuppressDisplayOnlyMetadataScope())
        {
            return ThingLabel(
                group.Representative,
                asRequirement,
                qualityRequirementMode,
                hitPointsRequirementMode,
                displayStackCount: group.Count,
                displayHitPointsLabel: HitPointsDisplayBucketLabel(group.Representative));
        }
    }

    private static IDisposable SuppressDisplayOnlyMetadataScope()
    {
        displayOnlyMetadataSuppressionDepth++;
        return new DisplayOnlyMetadataSuppressionScope();
    }

    private sealed class DisplayOnlyMetadataSuppressionScope : IDisposable
    {
        public void Dispose()
        {
            displayOnlyMetadataSuppressionDepth = Math.Max(0, displayOnlyMetadataSuppressionDepth - 1);
        }
    }

    private static string PawnDisplayGroupLabel(
        TradeThingDisplayGroup group,
        bool asRequirement,
        string? qualityRequirementMode,
        string? hitPointsRequirementMode)
    {
        ModThingReferenceDto thing = group.Representative;
        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        string label = def?.label?.CapitalizeFirst()
            ?? thing.DefName
            ?? thing.PawnPackage?.Identity?.ThingDef
            ?? ClashOfRimText.Key("ClashOfRim.UnknownItem");

        var details = new List<string>(2);
        string? genderLabel = PawnDisplayGenderLabel(def, PawnGenderValue(thing, def));
        if (!string.IsNullOrWhiteSpace(genderLabel))
        {
            details.Add(genderLabel!);
        }

        string? lifeStageLabel = PawnDisplayLifeStageLabel(def, PawnBiologicalAgeTicks(thing));
        if (!string.IsNullOrWhiteSpace(lifeStageLabel))
        {
            details.Add(lifeStageLabel!);
        }

        if (details.Count > 0)
        {
            label += " (" + string.Join(", ", details) + ")";
        }

        string suffix = FormatConditionSuffix(thing, asRequirement, false, qualityRequirementMode, hitPointsRequirementMode);
        return label
            + suffix
            + " x"
            + group.Count.ToString(CultureInfo.InvariantCulture);
    }

    private static string? PawnDisplayGenderLabel(ThingDef? def, string? genderValue)
    {
        if (string.IsNullOrWhiteSpace(genderValue)
            || !Enum.TryParse(genderValue, ignoreCase: true, out Gender gender)
            || gender == Gender.None)
        {
            return null;
        }

        return gender.GetLabel(def?.race?.Animal == true).CapitalizeFirst();
    }

    private static string? PawnGenderValue(ModThingReferenceDto thing, ThingDef? def)
    {
        string? explicitGender = thing.PawnPackage?.Identity?.Gender
            ?? PawnMetadataValue(thing, TradePawnUtility.PawnMetadataGender);
        if (!string.IsNullOrWhiteSpace(explicitGender))
        {
            return explicitGender;
        }

        return InferPawnGenderFromDisplayLabel(thing.DisplayLabel, def);
    }

    private static string? InferPawnGenderFromDisplayLabel(string? displayLabel, ThingDef? def)
    {
        if (string.IsNullOrWhiteSpace(displayLabel))
        {
            return null;
        }

        bool animal = def?.race?.Animal == true;
        foreach (Gender gender in new[] { Gender.Male, Gender.Female })
        {
            string label = gender.GetLabel(animal);
            if (!string.IsNullOrWhiteSpace(label)
                && displayLabel!.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return gender.ToString();
            }
        }

        return null;
    }

    private static long? PawnBiologicalAgeTicks(ModThingReferenceDto thing)
    {
        if (thing.PawnPackage?.Status?.BiologicalAgeTicks is long packageAgeTicks)
        {
            return packageAgeTicks;
        }

        string? metadataAgeTicks = PawnMetadataValue(thing, TradePawnUtility.PawnMetadataBiologicalAgeTicks);
        if (long.TryParse(metadataAgeTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            && parsed >= 0)
        {
            return parsed;
        }

        return null;
    }

    private static string? PawnMetadataValue(ModThingReferenceDto thing, string key)
    {
        return thing.Metadata is not null && thing.Metadata.TryGetValue(key, out string? value)
            ? value
            : null;
    }

    private static string? PawnDisplayLifeStageLabel(ThingDef? def, long? biologicalAgeTicks)
    {
        LifeStageAge? lifeStage = PawnDisplayLifeStage(def, biologicalAgeTicks);
        return lifeStage?.def?.label?.CapitalizeFirst();
    }

    private static void CacheThingLabel(string cacheKey, string label)
    {
        if (ThingLabelCache.Count > 1024)
        {
            ThingLabelCache.Clear();
        }

        ThingLabelCache[cacheKey] = label;
    }

    private static bool ShouldUseConcreteRequirementLabel(ModThingReferenceDto thing)
    {
        return !string.IsNullOrWhiteSpace(thing.Quality)
            || !string.IsNullOrWhiteSpace(thing.MinifiedInnerQuality)
            || !string.IsNullOrWhiteSpace(thing.StuffDefName)
            || !string.IsNullOrWhiteSpace(thing.MinifiedInnerStuffDefName);
    }

    private static string ConcreteThingLabel(ModThingReferenceDto thing, ThingDef? def)
    {
        string cacheKey = ConcreteThingLabelCacheKey(thing);
        if (ConcreteThingLabelCache.TryGetValue(cacheKey, out string cached))
        {
            return cached;
        }

        string label;
        if (def?.category == ThingCategory.Pawn && !string.IsNullOrWhiteSpace(thing.DisplayLabel))
        {
            label = thing.DisplayLabel!;
        }
        else if (!ShouldAvoidConcreteThingConstruction(thing)
            && TradeThingReferenceUtility.TryMakeThing(thing, Math.Max(1, thing.StackCount), out Thing? concreteThing, out _)
            && concreteThing is not null)
        {
            label = SafeConcreteThingLabel(concreteThing, thing, def);
        }
        else
        {
            label = !string.IsNullOrWhiteSpace(thing.DisplayLabel)
                ? thing.DisplayLabel!
                : def?.label?.CapitalizeFirst() ?? thing.DefName ?? ClashOfRimText.Key("ClashOfRim.UnknownItem");
        }

        if (ConcreteThingLabelCache.Count > 512)
        {
            ConcreteThingLabelCache.Clear();
        }

        ConcreteThingLabelCache[cacheKey] = label;
        return label;
    }

    private static string SafeConcreteThingLabel(Thing concreteThing, ModThingReferenceDto thing, ThingDef? def)
    {
        try
        {
            return concreteThing.LabelCapNoCount;
        }
        catch (NullReferenceException) when (concreteThing is Pawn)
        {
            return !string.IsNullOrWhiteSpace(thing.DisplayLabel)
                ? thing.DisplayLabel!
                : def?.label?.CapitalizeFirst() ?? thing.DefName ?? ClashOfRimText.Key("ClashOfRim.UnknownItem");
        }
    }

    private static bool ShouldAvoidConcreteThingConstruction(ModThingReferenceDto thing)
    {
        if (TradePawnUtility.IsPawnReference(thing))
        {
            return true;
        }

        if (thing.UniqueWeapon == true)
        {
            return true;
        }

        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        return TradeThingReferenceUtility.IsBookDef(def);
    }

    private static string ConcreteThingLabelCacheKey(ModThingReferenceDto thing)
    {
        var builder = new StringBuilder(256);
        AppendCacheKeyPart(builder, thing.DefName);
        AppendCacheKeyPart(builder, thing.GlobalKey);
        AppendCacheKeyPart(builder, thing.DisplayLabel);
        AppendCacheKeyPart(builder, thing.PawnPackageId);
        AppendCacheKeyPart(builder, thing.PawnPackage?.Reference?.GlobalId);
        AppendCacheKeyPart(builder, thing.PawnPackage?.Scribe?.XmlSha256);
        AppendCacheKeyPart(builder, thing.Quality);
        AppendCacheKeyPart(builder, thing.HitPoints?.ToString(CultureInfo.InvariantCulture));
        AppendCacheKeyPart(builder, thing.StuffDefName);
        AppendCacheKeyPart(builder, thing.MinifiedInnerDefName);
        AppendCacheKeyPart(builder, thing.MinifiedInnerStuffDefName);
        AppendCacheKeyPart(builder, thing.MinifiedInnerQuality);
        AppendCacheKeyPart(builder, thing.MinifiedInnerHitPoints?.ToString(CultureInfo.InvariantCulture));
        AppendCacheKeyPart(builder, thing.WornByCorpse?.ToString());
        AppendCacheKeyPart(builder, thing.Biocoded?.ToString());
        AppendCacheKeyPart(builder, thing.BiocodedPawnLabel);
        foreach (string part in ClashOfRimCompatibilityApi.ThingReferenceCacheKeyParts(thing))
        {
            AppendCacheKeyPart(builder, part);
        }

        return builder.ToString();
    }

    private static string ThingLabelCacheKey(
        ModThingReferenceDto thing,
        bool asRequirement,
        string? qualityRequirementMode,
        string? hitPointsRequirementMode,
        int? displayStackCount,
        string? displayHitPointsLabel)
    {
        var builder = new StringBuilder(320);
        AppendCacheKeyPart(builder, ConcreteThingLabelCacheKey(thing));
        AppendCacheKeyPart(builder, asRequirement ? "req" : "item");
        AppendCacheKeyPart(builder, qualityRequirementMode);
        AppendCacheKeyPart(builder, hitPointsRequirementMode);
        AppendCacheKeyPart(builder, displayHitPointsLabel);
        AppendCacheKeyPart(builder, (displayStackCount ?? thing.StackCount).ToString(CultureInfo.InvariantCulture));
        AppendCacheKeyPart(builder, thing.MaxHitPoints?.ToString(CultureInfo.InvariantCulture));
        AppendCacheKeyPart(builder, thing.MinifiedInnerMaxHitPoints?.ToString(CultureInfo.InvariantCulture));
        AppendCacheKeyPart(builder, thing.UniqueWeapon?.ToString());
        AppendCacheKeyPart(builder, TradeThingReferenceUtility.WeaponTraitKind(thing));
        AppendCacheKeyPart(builder, thing.MarketValue?.ToString(CultureInfo.InvariantCulture));
        if (thing.UniqueWeaponTraits is not null && thing.UniqueWeaponTraits.Count > 0)
        {
            foreach (string trait in thing.UniqueWeaponTraits)
            {
                AppendCacheKeyPart(builder, trait);
            }
        }

        return builder.ToString();
    }

    private static void AppendCacheKeyPart(StringBuilder builder, string? value)
    {
        if (builder.Length > 0)
        {
            builder.Append('\u001f');
        }

        builder.Append(value ?? string.Empty);
    }

    public static string ThingLabel(Thing thing)
    {
        if (thing is Pawn pawn)
        {
            return TradePawnUtility.PawnTradeLabel(pawn);
        }

        return thing.LabelCap + FormatThingConditionSuffix(thing);
    }

    public static string FormatThingList(IReadOnlyCollection<ModThingReferenceDto>? things, bool asRequirement = true)
    {
        if (things is null || things.Count == 0)
        {
            return ClashOfRimText.Key("ClashOfRim.None");
        }

        IReadOnlyList<TradeThingDisplayGroup> groups = BuildDisplayGroups(things.ToList());
        return string.Join(
            ClashOfRimText.Key("ClashOfRim.ListSeparator"),
            groups.Select(group => ThingDisplayGroupLabel(group, asRequirement)));
    }

    public static bool TryEstimateUnitMarketValue(ModThingReferenceDto thing, out float marketValue)
    {
        marketValue = 0f;
        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        if (def is null)
        {
            return false;
        }

        ThingDef? stuff = ResolveThingDef(thing.MinifiedInnerStuffDefName ?? thing.StuffDefName);
        if (TradeThingReferenceUtility.IsBookDef(def))
        {
            marketValue = Mathf.Max(0f, def.GetStatValueAbstract(StatDefOf.MarketValue, stuff));
            return true;
        }

        if (thing.UniqueWeapon == true)
        {
            if (thing.MarketValue.HasValue)
            {
                marketValue = Mathf.Max(0f, thing.MarketValue.Value);
                return true;
            }

            marketValue = Mathf.Max(0f, def.GetStatValueAbstract(StatDefOf.MarketValue, stuff));
            return true;
        }

        if (TradeThingReferenceUtility.TryMakeThing(thing, stackCount: 1, out Thing? concreteThing, out _)
            && concreteThing is not null)
        {
            marketValue = Mathf.Max(0f, concreteThing.MarketValue);
            return true;
        }

        marketValue = Mathf.Max(0f, def.GetStatValueAbstract(StatDefOf.MarketValue, stuff));
        return true;
    }

    public static bool TryEstimateTotalMarketValue(ModThingReferenceDto thing, out float marketValue)
    {
        marketValue = 0f;
        if (!TryEstimateUnitMarketValue(thing, out float unitMarketValue))
        {
            return false;
        }

        marketValue = unitMarketValue * Math.Max(1, thing.StackCount);
        return true;
    }

    public static string FormatMarketValue(float marketValue)
    {
        return Mathf.Max(0f, marketValue).ToStringMoney();
    }

    public static string FormatEstimatedUnitMarketValue(ModThingReferenceDto thing)
    {
        return TryEstimateUnitMarketValue(thing, out float marketValue)
            ? FormatMarketValue(marketValue)
            : ClashOfRimText.Key("ClashOfRim.Unknown");
    }

    public static string FormatConditionSuffix(
        ModThingReferenceDto thing,
        bool asRequirement,
        string? qualityRequirementMode = null,
        string? hitPointsRequirementMode = null)
    {
        return FormatConditionSuffix(thing, asRequirement, suppressConcreteRequirementMetadata: false, qualityRequirementMode, hitPointsRequirementMode);
    }

    private static string FormatConditionSuffix(
        ModThingReferenceDto thing,
        bool asRequirement,
        bool suppressConcreteRequirementMetadata,
        string? qualityRequirementMode = null,
        string? hitPointsRequirementMode = null,
        string? displayHitPointsLabel = null)
    {
        List<string> parts = new();
        if (asRequirement
            && (!suppressConcreteRequirementMetadata || string.Equals(qualityRequirementMode, "AtMost", StringComparison.Ordinal))
            && !string.IsNullOrWhiteSpace(thing.Quality))
        {
            string quality = FormatQualityLabel(thing.Quality);
            parts.Add(ClashOfRimText.Key(
                string.Equals(qualityRequirementMode, "AtMost", StringComparison.Ordinal)
                    ? "ClashOfRim.ItemQualityAtMost"
                    : "ClashOfRim.ItemQualityAtLeast",
                quality.Named("QUALITY")));
        }

        string? stuffDefName = thing.MinifiedInnerStuffDefName ?? thing.StuffDefName;
        if (asRequirement && !suppressConcreteRequirementMetadata && !string.IsNullOrWhiteSpace(stuffDefName))
        {
            parts.Add(ClashOfRimText.Key(
                "ClashOfRim.Trade.StuffRequirement",
                TradeThingReferenceUtility.StuffLabel(stuffDefName).Named("STUFF")));
        }

        ClashOfRimCompatibilityApi.AppendThingReferenceDisplayParts(thing, asRequirement, parts);

        if ((thing.MinifiedInnerHitPoints ?? thing.HitPoints).HasValue)
        {
            string hitPoints = displayHitPointsLabel ?? FormatHitPointsPercent(thing);
            parts.Add(asRequirement
                ? (string.Equals(hitPointsRequirementMode, "AtMost", StringComparison.Ordinal) ? "<=" : ">=") + hitPoints
                : hitPoints);
        }

        AddSpecialThingTags(parts, thing.WornByCorpse, thing.Biocoded, thing.BiocodedPawnLabel);
        AddUniqueWeaponTags(
            parts,
            thing.UniqueWeapon,
            thing.UniqueWeaponTraits,
            thing.MarketValue,
            asRequirement,
            TradeThingReferenceUtility.WeaponTraitKind(thing));
        if (!asRequirement)
        {
            AddFixedFeeTag(parts, thing.DefName);
        }

        return parts.Count == 0 ? string.Empty : "（" + string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), parts) + "）";
    }

    public static string FormatThingConditionSuffix(Thing thing)
    {
        List<string> parts = new();
        if (thing.def.useHitPoints)
        {
            parts.Add(FormatHitPointsPercent(thing.HitPoints, thing.MaxHitPoints));
        }

        if (thing.Stuff is not null)
        {
            parts.Add(thing.Stuff.LabelCap);
        }

        bool? wornByCorpse = thing is Apparel apparel ? apparel.WornByCorpse : null;
        CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
        bool biocoded = biocodable?.Biocoded == true;
        AddSpecialThingTags(parts, wornByCorpse, biocoded ? true : null, biocoded ? biocodable?.CodedPawnLabel : null);
        AddUniqueWeaponTags(
            parts,
            TradeThingReferenceUtility.IsWeaponWithTraits(thing) ? true : null,
            TradeThingReferenceUtility.WeaponTraitDefNames(thing),
            thing.MarketValue,
            false,
            TradeThingReferenceUtility.WeaponTraitKind(thing));
        AddFixedFeeTag(parts, thing.def.defName);

        return parts.Count == 0 ? string.Empty : "（" + string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), parts) + "）";
    }

    public static bool DefSupportsQuality(ThingDef? def)
    {
        return def is not null
            && !ClashOfRimCompatibilityApi.SuppressesStandardThingStats(def)
            && def.HasComp(typeof(CompQuality));
    }

    public static bool DefSupportsHitPoints(ThingDef? def)
    {
        return def is not null
            && !ClashOfRimCompatibilityApi.SuppressesStandardThingStats(def)
            && def.useHitPoints
            && def.BaseMaxHitPoints > 0;
    }

    public static int DefaultHitPoints(ThingDef def)
    {
        return Math.Max(1, def.BaseMaxHitPoints);
    }

    public static int HitPointsFromPercent(ThingDef def, int percent)
    {
        int max = DefaultHitPoints(def);
        return Math.Max(1, Mathf.CeilToInt(max * Mathf.Clamp(percent, 1, 100) / 100f));
    }

    public static int HitPointsPercent(int hitPoints, ThingDef def)
    {
        return HitPointsPercent(hitPoints, DefaultHitPoints(def));
    }

    public static string FormatHitPointsPercent(int hitPoints, ThingDef? def)
    {
        return def is null
            ? hitPoints.ToString()
            : HitPointsPercent(hitPoints, def) + "%";
    }

    public static string FormatHitPointsPercent(ModThingReferenceDto thing)
    {
        int? hitPoints = thing.MinifiedInnerHitPoints ?? thing.HitPoints;
        if (!hitPoints.HasValue)
        {
            return string.Empty;
        }

        int? maxHitPoints = thing.MinifiedInnerMaxHitPoints ?? thing.MaxHitPoints;
        if (maxHitPoints.HasValue && maxHitPoints.Value > 0)
        {
            return FormatHitPointsPercent(hitPoints.Value, maxHitPoints.Value);
        }

        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        return FormatHitPointsPercent(hitPoints.Value, def);
    }

    public static string FormatHitPointsPercent(int hitPoints, int maxHitPoints)
    {
        return HitPointsPercent(hitPoints, Math.Max(1, maxHitPoints)) + "%";
    }

    public static string FormatQualityLabel(string? quality)
    {
        return Enum.TryParse(quality, ignoreCase: true, out QualityCategory parsed)
            ? FormatQualityLabel(parsed)
            : quality ?? string.Empty;
    }

    public static string FormatQualityLabel(QualityCategory quality)
    {
        return quality.GetLabel().CapitalizeFirst();
    }

    public static string FormatOrderStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return ClashOfRimText.Key("ClashOfRim.Unknown");
        }

        string statusValue = status!;
        return statusValue switch
        {
            "PendingOfflineDelivery" => ClashOfRimText.Key("ClashOfRim.Trade.StatusOpen"),
            "ReadyForImmediateDelivery" => ClashOfRimText.Key("ClashOfRim.Trade.StatusOpen"),
            "Recorded" => ClashOfRimText.Key("ClashOfRim.Trade.StatusOpen"),
            "AppliedToSnapshot" => ClashOfRimText.Key("ClashOfRim.Trade.StatusCompleted"),
            "Cancelled" => ClashOfRimText.Key("ClashOfRim.Trade.StatusCancelled"),
            "Failed" => ClashOfRimText.Key("ClashOfRim.Trade.StatusFailed"),
            "RejectedByTarget" => ClashOfRimText.Key("ClashOfRim.Trade.StatusRejected"),
            _ => statusValue
        };
    }

    public static string FormatPostage(ModTradePostageQuoteDto? postage)
    {
        if (postage is null)
        {
            return ClashOfRimText.Key("ClashOfRim.Trade.PostageNotCalculated");
        }

        if (!postage.Reachable || !postage.PostageSilver.HasValue)
        {
            return ClashOfRimText.Key("ClashOfRim.Trade.PostageUnreachable")
                + (string.IsNullOrWhiteSpace(postage.Status) ? string.Empty : ": " + postage.Status);
        }

        string distance = postage.DistanceTiles.HasValue
            ? ClashOfRimText.Key("ClashOfRim.Trade.DistanceSuffix", postage.DistanceTiles.Value.Named("DISTANCE"))
            : string.Empty;
        return ClashOfRimText.Key("ClashOfRim.Trade.PostageSilver", postage.PostageSilver.Value.Named("SILVER"), distance.Named("DISTANCE"));
    }

    public static string FormatTradeExpiry(ModTradeOrderSummaryDto order)
    {
        if (string.IsNullOrWhiteSpace(order.ExpiresAtUtc)
            || !DateTimeOffset.TryParse(order.ExpiresAtUtc, out DateTimeOffset expiresAtUtc))
        {
            return ClashOfRimText.Key("ClashOfRim.Unknown");
        }

        TimeSpan remaining = expiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return ClashOfRimText.Key("ClashOfRim.Trade.Expired");
        }

        return ClashOfRimText.Key(
            "ClashOfRim.Trade.ExpiresIn",
            FormatRealTimeRemaining(remaining).Named("TIME"));
    }

    public static string FormatRealTimeRemaining(TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1)
        {
            return ClashOfRimText.Key("ClashOfRim.Time.DaysHours", Math.Floor(remaining.TotalDays).Named("DAYS"), remaining.Hours.Named("HOURS"));
        }

        if (remaining.TotalHours >= 1)
        {
            return ClashOfRimText.Key("ClashOfRim.Time.HoursMinutes", Math.Floor(remaining.TotalHours).Named("HOURS"), remaining.Minutes.Named("MINUTES"));
        }

        return ClashOfRimText.Key("ClashOfRim.Time.Minutes", Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)).Named("MINUTES"));
    }

    private static int HitPointsPercent(int hitPoints, int maxHitPoints)
    {
        return Mathf.Clamp(Mathf.RoundToInt(hitPoints * 100f / Math.Max(1, maxHitPoints)), 1, 100);
    }

    private static void AddSpecialThingTags(
        List<string> parts,
        bool? wornByCorpse,
        bool? biocoded,
        string? biocodedPawnLabel)
    {
        if (wornByCorpse == true)
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.ItemWornByCorpse"));
        }

        if (biocoded == true)
        {
            parts.Add(string.IsNullOrWhiteSpace(biocodedPawnLabel)
                ? ClashOfRimText.Key("ClashOfRim.ItemBiocoded")
                : ClashOfRimText.Key("ClashOfRim.ItemBiocodedTo", biocodedPawnLabel.Named("PAWN")));
        }
    }

    private static void AddUniqueWeaponTags(
        List<string> parts,
        bool? uniqueWeapon,
        IEnumerable<string>? uniqueWeaponTraits,
        float? marketValue,
        bool asRequirement,
        string? weaponTraitKind)
    {
        if (uniqueWeapon != true)
        {
            return;
        }

        parts.Add(ClashOfRimText.Key(WeaponTraitLabelKey(weaponTraitKind)));
        string[] traits = (uniqueWeaponTraits ?? Array.Empty<string>())
            .Where(trait => !string.IsNullOrWhiteSpace(trait))
            .Select(UniqueWeaponTraitLabel)
            .ToArray();
        if (traits.Length > 0)
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.ItemUniqueWeaponTraits", string.Join("/", traits).Named("TRAITS")));
        }

        if (!asRequirement && marketValue.HasValue)
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.ItemMarketValue", marketValue.Value.ToString("0.##", CultureInfo.InvariantCulture).Named("VALUE")));
        }
    }

    private static string UniqueWeaponTraitLabel(string traitDefName)
    {
        WeaponTraitDef? trait = DefDatabase<WeaponTraitDef>.GetNamedSilentFail(traitDefName);
        return trait?.LabelCap.ToString() ?? traitDefName;
    }

    private static string WeaponTraitLabelKey(string? weaponTraitKind)
    {
        return weaponTraitKind switch
        {
            TradeThingReferenceUtility.WeaponTraitKindPersona => "ClashOfRim.ItemPersonaWeapon",
            TradeThingReferenceUtility.WeaponTraitKindSpecialized => "ClashOfRim.ItemSpecializedWeapon",
            _ => "ClashOfRim.ItemSpecializedWeapon"
        };
    }

    private static void AddFixedFeeTag(List<string> parts, string? defName)
    {
        if (string.IsNullOrWhiteSpace(defName))
        {
            return;
        }

        string defKey = defName!;
        if (DefaultFixedFeePerThing.TryGetValue(defKey, out int perThingFee)
            && perThingFee > 0)
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.FixedCustodyFeePerThing", perThingFee.Named("FEE")));
        }
    }
}
