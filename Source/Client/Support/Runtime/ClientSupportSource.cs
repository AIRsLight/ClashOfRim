using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

internal sealed class ClientSupportSource : IClientSupportSource
{
    private readonly Game game;
    private readonly Caravan caravan;
    private readonly Pawn pawn;
    internal ClientSupportSource(Caravan caravan, Pawn pawn)
    { game = Current.Game; this.caravan = caravan; this.pawn = pawn; }

    public ClientSupportCapture Capture(ClashOfRimClientNetworkContext context, ClientSupportTarget target)
    {
        if (Validate(target) is { } rejection) return new(null, rejection);
        string key = PawnGlobalIdUtility.Build(context.UserId, pawn);
        var package = new PawnExchangeCapture(context.UserId, context.ColonyId, context.CurrentSnapshotId).Capture(pawn, key);
        if (package.Scribe is null || string.IsNullOrWhiteSpace(package.Scribe.Xml))
            return new(null, ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusMissingScribe",
                ClashOfRimText.Key("ClashOfRim.PawnExchange.LabelSupport").Named("LABEL")));
        // Both values must be read before removing the last pawn destroys its caravan.
        return new(new(key, pawn.LabelShort, caravan.Tile, caravan.GetUniqueLoadID(), package));
    }

    public string? Remove(ClientSupportCommand command, Action beforeMutation)
    {
        if (Validate(command.Target) is { } rejection) return rejection;
        beforeMutation();
        caravan.RemovePawn(pawn);
        if (!caravan.Destroyed && caravan.PawnsListForReading?.Count == 0)
        {
            caravan.Destroy();
            ClashLog.Message("[ClashOfRim][Support] Removed empty source caravan after sending its last pawn as support.");
        }
        if (!command.Selection.Permanent && !SupportPawnWorldPawnContextUtility.MarkDepartingTemporarySupportPawn(
            pawn, command.Pawn.GlobalKey, out string failure))
            Log.Warning("[ClashOfRim][Support] Temporary support pawn was not preserved as world pawn context: " + failure + ".");
        return null;
    }

    private string? Validate(ClientSupportTarget target)
    {
        if (!ReferenceEquals(Current.Game, game) || caravan.Destroyed || pawn.Destroyed || pawn.Dead
            || !caravan.ContainsPawn(pawn) || !caravan.IsOwner(pawn))
            return ClashOfRimText.Key("ClashOfRim.Support.StatusPawnNotInCaravan");
        if (target.Tile != (int)caravan.Tile || target.TileLayerId != RemoteWorldObjectUiUtility.ReadTileLayerId(caravan.Tile))
            return ClashOfRimText.Key("ClashOfRim.Support.StatusTargetTileMismatch");
        return null;
    }
}
