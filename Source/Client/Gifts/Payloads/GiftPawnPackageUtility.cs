using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

internal static class GiftPawnPackageUtility
{
    public static ModPawnExchangePackageDto BuildPackage(Pawn pawn, string globalKey, string userId,
        string colonyId, string snapshotId, string containerKey) =>
        new PawnExchangeCapture(userId, colonyId, snapshotId).Capture(pawn, globalKey, containerKey);

    public static bool TryRestore(ModPawnExchangePackageDto package, out Pawn? pawn, out string message)
    {
        return PawnExchangeRestoreService.TryRestore(
            package,
            PawnExchangeRestoreKind.AnimalGift,
            out pawn,
            out message);
    }

    public static bool TryRestoreTradePawn(ModPawnExchangePackageDto package, out Pawn? pawn, out string message)
    {
        return PawnExchangeRestoreService.TryRestore(
            package,
            PawnExchangeRestoreKind.TradePawn,
            out pawn,
            out message);
    }

    public static bool TryRestoreGiftPawn(ModPawnExchangePackageDto package, out Pawn? pawn, out string message)
    {
        return PawnExchangeRestoreService.TryRestore(
            package,
            PawnExchangeRestoreKind.GiftPawn,
            out pawn,
            out message);
    }

    public static bool TryRestoreCorpsePawn(ModPawnExchangePackageDto package, out Pawn? pawn, out string message)
    {
        return PawnExchangeRestoreService.TryRestore(
            package,
            PawnExchangeRestoreKind.CorpseGift,
            out pawn,
            out message,
            forcePlayerFaction: false);
    }

}
