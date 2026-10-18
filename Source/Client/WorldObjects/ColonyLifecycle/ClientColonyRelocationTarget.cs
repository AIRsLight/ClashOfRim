using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientColonyRelocationTarget : IClientColonyRelocationTarget
{
    private readonly Game? game = Current.Game;
    private readonly World? world = Find.World;
    private readonly WorldObject target;
    private readonly MapParent? old;
    private readonly Map? map;
    private readonly bool implicitMove, requiresNoEnemies;
    internal ClientColonyRelocationTarget(WorldObject target, Map? map = null, bool requiresNoEnemies = false, bool implicitMove = false)
    {
        this.target = target; this.map = map; this.requiresNoEnemies = requiresNoEnemies; this.implicitMove = implicitMove;
        old = Homes().FirstOrDefault(); Tile = target.Tile.tileId; Layer = ClientWorldRuntimeValues.ReadFirstTileLayerId(target, "Tile", "tile");
    }
    public bool IsCurrent => ReferenceEquals(game, Current.Game) && ReferenceEquals(world, Find.World);
    public bool IsAvailable => IsCurrent && !target.Destroyed && world?.worldObjects?.AllWorldObjects.Contains(target) == true
        && target.Tile.tileId == Tile && ClientWorldRuntimeValues.ReadFirstTileLayerId(target, "Tile", "tile") == Layer
        && (implicitMove ? map is not null && game!.Maps.Contains(map) && (map.IsPlayerHome || map.ParentFaction == Faction.OfPlayer)
            : old is not null && !old.Destroyed && Homes().Contains(old)
                && (map is not null ? game!.Maps.Contains(map) && ReferenceEquals(map.Parent, target) && !map.IsPlayerHome
                    : target is Caravan caravan && caravan.Faction == Faction.OfPlayer && caravan.IsPlayerControlled));
    public bool HasRemoteSession => ClashOfRimGameComponent.HasActiveRemoteMapSession || ClashOfRimGameComponent.HasActiveRaidBattleSession;
    public int HomeCount => Homes().Count;
    public string OldLabel => old?.LabelCap ?? "";
    public int Tile { get; }
    public int Layer { get; }
    public string? SettleFailure
    {
        get
        {
            if (implicitMove) return null;
            if (map is not null)
            {
                if (map.mapPawns.FreeColonistsCount == 0) return "CommandSettleFailNoColonists".Translate();
                if (requiresNoEnemies && map.attackTargetsCache.TargetsHostileToColony.Any(target => GenHostility.IsActiveThreatToPlayer(target)))
                    return "CommandSettleFailEnemies".Translate();
                return null;
            }
            var command = ClientColonyRelocationCommands.VanillaCommand((Caravan)target);
            return NonLimitFailure(command);
        }
    }
    internal static string? NonLimitFailure(Command command)
    {
        if (!command.Disabled) return null;
        string? reason = command.disabledReason;
        if (string.IsNullOrWhiteSpace(reason)) return reason ?? string.Empty;
        return reason == "CommandSettleFailAlreadyHaveBase".Translate().ToString()
            || reason == "CommandSettleFailReachedMaximumNumberOfBases".Translate().ToString() ? null : reason;
    }
    public void Settle()
    {
        SoundDefOf.Tick_High.PlayOneShotOnCamera();
        if (map is not null) SettleInExistingMapUtility.Settle(map);
        else SettleInEmptyTileUtility.Settle((Caravan)target);
    }
    public void EnqueueSettled(Action action, Action<Exception> failed)
        => LongEventHandler.QueueLongEvent(action, "ClashOfRim.ColonyRelocation.Generating".Translate(), false, failed);
    public void AbandonOld() { if (old is not null && !old.Destroyed) old.Abandon(false); }
    public bool IsSingleDestination
    {
        get { var homes = Homes(); return IsCurrent && homes.Count == 1 && homes[0].Tile.tileId == Tile && ClientWorldRuntimeValues.ReadFirstTileLayerId(homes[0], "Tile", "tile") == Layer; }
    }
    private List<MapParent> Homes() => IsCurrent ? world?.worldObjects?.AllWorldObjects.OfType<MapParent>()
        .Where(parent => parent.Faction == Faction.OfPlayer && parent.HasMap && parent.Map?.IsPlayerHome == true)
        .GroupBy(parent => parent.GetUniqueLoadID(), StringComparer.Ordinal).Select(group => group.First()).ToList() ?? new() : new();
}
