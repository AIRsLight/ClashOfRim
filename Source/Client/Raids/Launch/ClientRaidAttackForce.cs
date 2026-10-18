using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Pawns;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

// Owns the engine objects behind the captured manifest. Async preparation must
// not rediscover another force, game or location when it eventually enters.
internal sealed class ClientRaidAttackForce
{
    private readonly Game game;
    private readonly IRaidAttackForceSource source;
    private IReadOnlyList<RaidAttackPawnLoad>? captured;
    private int tile, layer;
    internal ClientRaidAttackForce(IRaidAttackForceSource source) { this.source = source; game = Current.Game; }

    internal bool TryCapture(ClashOfRimClientNetworkContext context, ModWorldMapMarkerDto target,
        out ClientRaidForceManifest? manifest, out string failure)
    {
        manifest = null; captured = null; tile = target.Tile; layer = target.TileLayerId;
        failure = LocationFailure() ?? string.Empty;
        if (failure.Length != 0) return false;
        var pawns = source.AttackPawns;
        if (pawns.Count == 0) { failure = ClashOfRimText.Key("ClashOfRim.Raid.StatusNoPawns"); return false; }
        var loads = pawns.Select(pawn => new RaidAttackPawnLoad(PawnGlobalIdUtility.Build(context.UserId, pawn), pawn)).ToArray();
        var things = source.BuildCarriedThings(context.UserId, context.ColonyId, context.CurrentSnapshotId ?? string.Empty);
        // Some metadata providers return shared nested objects; freeze the wire intent once.
        var cargo = ClientProtocolJson.Deserialize<List<ModThingReferenceDto>>(ClientProtocolJson.Serialize(things.ToList()), type => type);
        manifest = new(source.UniqueLoadId, loads.Select(load => load.GlobalKey).ToArray(), cargo);
        captured = loads;
        return true;
    }

    internal string? EntryFailure()
    {
        if (LocationFailure() is { } location) return location;
        if (captured is null) return ClashOfRimText.Key("ClashOfRim.Raid.StatusNoPawns");
        var available = source.AttackPawns;
        return captured.Any(load => load.Pawn.Dead || load.Pawn.Destroyed || !available.Contains(load.Pawn))
            ? ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerPawnUnavailable") : null;
    }

    internal bool TryEnterBattle(ActiveRaidBattleSession session, Map? map, out string failure)
    {
        failure = EntryFailure() ?? string.Empty;
        if (failure.Length != 0) return false;
        if (map is null)
        {
            failure = ClashOfRimText.Key("ClashOfRim.Raid.StatusMissingPreparedBattleMap", session.EventId.Named("EVENTID"));
            return false;
        }
        return ClientRaidBattlefield.TryEnter(session, map, source, captured!, out failure);
    }

    private string? LocationFailure()
    {
        if (!ReferenceEquals(Current.Game, game) || !source.IsAvailable)
            return ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerPawnUnavailable");
        return source.Tile != tile || source.TileLayerId != layer
            ? ClashOfRimText.Key("ClashOfRim.Raid.StatusTargetTileMismatch") : null;
    }
}
