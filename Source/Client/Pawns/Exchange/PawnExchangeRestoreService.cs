using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using System;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnExchangeRestoreService
{
    public static bool TryRestore(
        ModPawnExchangePackageDto package,
        PawnExchangeRestoreKind kind,
        out Pawn? pawn,
        out string message,
        bool forcePlayerFaction = true)
    {
        ClashLog.Message(
            "[ClashOfRim][PawnExchange] restore requested kind="
            + kind
            + " global="
            + (package.Reference?.GlobalId ?? "<null>")
            + " thingDef="
            + (package.Identity?.ThingDef ?? "<null>")
            + " extensions="
            + package.Extensions.Count
            + " scribeBytes="
            + (package.Scribe?.Xml?.Length ?? 0)
            + ".");
        ClashOfRimCompatibilityApi.NormalizePawnExchangePackage(package);
        ClashLog.Message(
            "[ClashOfRim][PawnExchange] restore normalized kind="
            + kind
            + " global="
            + (package.Reference?.GlobalId ?? "<null>")
            + " metadataKeys="
            + (package.Reference?.Metadata is null ? string.Empty : string.Join(",", package.Reference.Metadata.Keys))
            + ".");
        return PawnExchangeScribeRestorer.TryRestore(
            package,
            restored => IsAllowed(restored, kind),
            ValidatorFailure(kind),
            Label(kind),
            out pawn,
            out message,
            forcePlayerFaction);
    }

    private static bool IsAllowed(Pawn pawn, PawnExchangeRestoreKind kind)
    {
        return kind switch
        {
            PawnExchangeRestoreKind.Support => true,
            PawnExchangeRestoreKind.AnimalGift => pawn.RaceProps?.Animal == true,
            PawnExchangeRestoreKind.TradePawn => pawn.RaceProps?.Animal == true
                || ClashOfRimCompatibilityApi.IsTradePawnRestoreAllowedByCompatibility(pawn),
            PawnExchangeRestoreKind.GiftPawn => pawn.RaceProps?.Animal == true
                || pawn.IsPrisoner
                || pawn.IsSlave,
            PawnExchangeRestoreKind.CorpseGift => pawn.Dead && pawn.RaceProps?.corpseDef is not null,
            _ => false
        };
    }

    private static string ValidatorFailure(PawnExchangeRestoreKind kind)
    {
        return kind switch
        {
            PawnExchangeRestoreKind.Support => ClashOfRimText.Key("ClashOfRim.PawnExchange.ValidatorSupport"),
            PawnExchangeRestoreKind.AnimalGift => ClashOfRimText.Key("ClashOfRim.PawnExchange.ValidatorAnimalGift"),
            PawnExchangeRestoreKind.TradePawn => ClashOfRimText.Key("ClashOfRim.PawnExchange.ValidatorTradePawn"),
            PawnExchangeRestoreKind.GiftPawn => ClashOfRimText.Key("ClashOfRim.PawnExchange.ValidatorGiftPawn"),
            PawnExchangeRestoreKind.CorpseGift => ClashOfRimText.Key("ClashOfRim.PawnExchange.ValidatorCorpseGift"),
            _ => ClashOfRimText.Key("ClashOfRim.PawnExchange.ValidatorInvalidKind")
        };
    }

    private static string Label(PawnExchangeRestoreKind kind)
    {
        return kind switch
        {
            PawnExchangeRestoreKind.Support => ClashOfRimText.Key("ClashOfRim.PawnExchange.LabelSupport"),
            PawnExchangeRestoreKind.AnimalGift => ClashOfRimText.Key("ClashOfRim.PawnExchange.LabelAnimal"),
            PawnExchangeRestoreKind.TradePawn => ClashOfRimText.Key("ClashOfRim.PawnExchange.LabelTradePawn"),
            PawnExchangeRestoreKind.GiftPawn => ClashOfRimText.Key("ClashOfRim.PawnExchange.LabelGiftPawn"),
            PawnExchangeRestoreKind.CorpseGift => ClashOfRimText.Key("ClashOfRim.PawnExchange.LabelCorpse"),
            _ => "pawn"
        };
    }
}
