using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal static class TradePawnUtility
{
    internal const string PawnMetadataThingDef = "clashofrim.pawn.thingDef";
    internal const string PawnMetadataPawnKindDef = "clashofrim.pawn.pawnKindDef";
    internal const string PawnMetadataGender = "clashofrim.pawn.gender";
    internal const string PawnMetadataBiologicalAgeTicks = "clashofrim.pawn.biologicalAgeTicks";
    internal const string PawnMetadataMinBiologicalAgeYears = "clashofrim.pawn.minBiologicalAgeYears";
    internal const string PawnMetadataMaxBiologicalAgeYears = "clashofrim.pawn.maxBiologicalAgeYears";

    private static readonly ITrader MarketplaceTrader = new MarketplacePawnSaleTrader();
    private static TraderKindDef? cachedMarketplaceTraderKind;

    public static bool IsTradeablePawn(Pawn pawn)
    {
        return pawn is { Destroyed: false, Dead: false }
            && pawn.Faction == Faction.OfPlayer
            && !pawn.IsQuestLodger()
            && IsTradeablePawnRace(pawn);
    }

    public static bool IsTradeablePawnRace(Pawn pawn)
    {
        return pawn.RaceProps?.Animal == true
            || pawn.RaceProps?.IsMechanoid == true
            || ClashOfRimCompatibilityApi.IsTradeablePawnByCompatibility(pawn);
    }

    public static bool IsPawnReference(ModThingReferenceDto reference)
    {
        ThingDef? def = TradeUiUtility.ResolveThingDef(reference.DefName);
        return reference.PawnPackage is not null
            || !string.IsNullOrWhiteSpace(reference.PawnPackageId)
            || def?.category == ThingCategory.Pawn;
    }

    public static string PawnTradeLabel(Pawn pawn)
    {
        string label = pawn.LabelCapNoCount;
        if (pawn.Name != null && !pawn.Name.Numerical && !pawn.RaceProps.Humanlike)
        {
            label = label + ", " + pawn.def.label;
        }

        return string.Concat(
            label,
            " (",
            pawn.GetGenderLabel(),
            ", ",
            Mathf.FloorToInt(pawn.ageTracker.AgeBiologicalYearsFloat).ToString(),
            ")");
    }

    public static void DrawPawnExtraIcons(Pawn pawn, Rect row, ref float curX)
    {
        if (pawn.RaceProps.Animal)
        {
            if (pawn.relations?.GetFirstDirectRelationPawn(PawnRelationDefOf.Bond, null) != null)
            {
                TransferableUIUtility.DrawBondedIcon(
                    pawn,
                    new Rect(curX - 24f, row.y + (row.height - 24f) / 2f, 24f, 24f));
                curX -= 24f;
            }

            if (pawn.health?.hediffSet?.HasHediff(HediffDefOf.Pregnant, true) == true)
            {
                TransferableUIUtility.DrawPregnancyIcon(
                    pawn,
                    new Rect(curX - 24f, row.y + (row.height - 24f) / 2f, 24f, 24f));
                curX -= 24f;
            }
        }
    }

    public static ModThingReferenceDto BuildPawnReference(
        Pawn pawn,
        string globalKey,
        string userId,
        string colonyId,
        string snapshotId,
        string containerKey)
    {
        return new ModThingReferenceDto
        {
            GlobalKey = globalKey,
            DefName = pawn.def.defName,
            StackCount = 1,
            DisplayLabel = PawnTradeLabel(pawn),
            MarketValue = pawn.MarketValue,
            Metadata = BuildPawnSummaryMetadata(pawn),
            PawnPackage = GiftPawnPackageUtility.BuildPackage(
                pawn,
                globalKey,
                userId,
                colonyId,
                snapshotId,
                containerKey)
        };
    }

    private static Dictionary<string, string?> BuildPawnSummaryMetadata(Pawn pawn)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [PawnMetadataThingDef] = pawn.def?.defName,
            [PawnMetadataPawnKindDef] = pawn.kindDef?.defName,
            [PawnMetadataGender] = pawn.gender.ToString(),
            [PawnMetadataBiologicalAgeTicks] = pawn.ageTracker?.AgeBiologicalTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return metadata;
    }

    public static void ApplySoldPawnEffectsAndRemove(Pawn pawn, Caravan? caravan = null)
    {
        Pawn? negotiator = pawn.MapHeld?.mapPawns?.FreeColonistsSpawned?.FirstOrDefault()
            ?? caravan?.PawnsListForReading.FirstOrDefault(candidate => candidate.RaceProps?.Humanlike == true && candidate.Faction == Faction.OfPlayer);
        ApplyBondedAnimalSoldEffect(pawn);
        ApplySoldPawnEffectsWithoutDroppingCarriedThings(pawn, negotiator);
        ClashOfRimCompatibilityApi.ApplyPawnSoldEffects(pawn, negotiator);

        if (pawn.Spawned)
        {
            pawn.DeSpawn(DestroyMode.Vanish);
        }

        caravan?.RemovePawn(pawn);
        if (!pawn.Destroyed)
        {
            pawn.Destroy(DestroyMode.Vanish);
        }
    }

    private static void ApplySoldPawnEffectsWithoutDroppingCarriedThings(Pawn pawn, Pawn? negotiator)
    {
        pawn.ownership?.UnclaimAll();
        if (pawn.RaceProps?.Humanlike == true)
        {
            TaleRecorder.RecordTale(TaleDefOf.SoldPrisoner, negotiator, pawn, MarketplaceTrader);
        }

        if (pawn.Faction != null)
        {
            pawn.SetFaction(null);
        }

        if (pawn.RaceProps?.IsFlesh == true)
        {
            pawn.relations?.Notify_PawnSold(negotiator);
        }

        pawn.guest?.SetGuestStatus(null, GuestStatus.Guest);
        pawn.ClearMind_NewTemp();
    }

    private static void ApplyBondedAnimalSoldEffect(Pawn pawn)
    {
        if (pawn.RaceProps?.Animal != true || pawn.relations is null)
        {
            return;
        }

        Pawn bondedPawn = pawn.relations.GetFirstDirectRelationPawn(PawnRelationDefOf.Bond, null);
        if (bondedPawn?.needs?.mood is null)
        {
            return;
        }

        pawn.relations.RemoveDirectRelation(PawnRelationDefOf.Bond, bondedPawn);
        bondedPawn.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.SoldMyBondedAnimalMood, null, null);
    }

    public static IEnumerable<Pawn> TradeableMapPawns(Map map)
    {
        return map.mapPawns.AllPawnsSpawned
            .Where(IsTradeablePawn)
            .OrderBy(pawn => pawn.def.label)
            .ThenBy(pawn => pawn.LabelShort);
    }

    private sealed class MarketplacePawnSaleTrader : ITrader
    {
        public TraderKindDef TraderKind => cachedMarketplaceTraderKind ??= DefDatabase<TraderKindDef>.AllDefsListForReading.FirstOrDefault();

        public IEnumerable<Thing> Goods => Enumerable.Empty<Thing>();

        public int RandomPriceFactorSeed => 0;

        public string TraderName => ClashOfRimText.Key("ClashOfRim.Trade.MarketTraderName");

        public bool CanTradeNow => true;

        public float TradePriceImprovementOffsetForPlayer => 0f;

        public Faction Faction => Faction.OfPlayer;

        public TradeCurrency TradeCurrency => TradeCurrency.Silver;

        public IEnumerable<Thing> ColonyThingsWillingToBuy(Pawn playerNegotiator)
        {
            return Enumerable.Empty<Thing>();
        }

        public void GiveSoldThingToTrader(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
        }

        public void GiveSoldThingToPlayer(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
        }
    }
}
