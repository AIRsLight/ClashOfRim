using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Caravans;

// Owns only the displayed caravan command. Menus and transactions stay with
// their domains; all four Harmony adapters share the same delayed UI boundary.
internal sealed class ClientCaravanGizmoContext
{
    private readonly ClientRequestTracker<Caravan> requests;
    private readonly ClientRequestTracker<Caravan>.Scope owner;
    private ClientCaravanGizmoContext(ClientSessionLifecycle session, Caravan caravan, Func<bool> bindingCurrent)
    {
        var game = Current.Game;
        var world = Find.World;
        var tile = caravan.Tile;
        int layer = ClientWorldRuntimeValues.ReadPlanetTileLayerId(tile);
        bool CurrentSource(Caravan source)
        {
            if (!bindingCurrent() || session.IsUnavailable || !ReferenceEquals(Current.Game, game)
                || !ReferenceEquals(Find.World, world) || !Available(source)) return false;
            var location = source.Tile;
            return location.tileId == tile.tileId && ClientWorldRuntimeValues.ReadPlanetTileLayerId(location) == layer;
        }
        requests = new(session, () => caravan, CurrentSource, static () => { });
        owner = requests.Capture();
    }
    internal static ClientCaravanGizmoContext? Capture(ClientSessionLifecycle session, Caravan? caravan, Func<bool> bindingCurrent)
        => Current.Game is not null && Find.World is not null && Available(caravan)
            ? new(session, caravan!, bindingCurrent) : null;
    internal bool Step(Action action) => requests.TryApply(owner, action) && requests.TryApply(owner, static () => { });

    internal void Append(ref IEnumerable<Gizmo> source, int count, string key, Func<Texture2D> icon,
        Action open, Func<bool>? canOpen = null)
    {
        if (count == 0) return;
        string label = "", description = "";
        Texture2D? texture = null;
        if (!Step(() => label = count == 1 ? ClashOfRimText.Key(key) : ClashOfRimText.Key(key + "Count", count.Named("COUNT")))
            || !Step(() => description = ClashOfRimText.Key(key + "Desc"))
            || !Step(() => texture = icon())) return;
        bool opened = false;
        source = source.Append(new Command_Action
        {
            defaultLabel = label, defaultDesc = description, icon = texture,
            action = () => Step(() =>
            {
                if (opened) return;
                bool eligible = canOpen?.Invoke() ?? true;
                if (eligible) Step(() => { opened = true; open(); });
            })
        });
    }
    internal static bool Available(Caravan? caravan)
        => caravan is not null && !caravan.Destroyed && caravan.Spawned && caravan.IsPlayerControlled;
    internal static bool HasItems(Caravan caravan)
        => CaravanInventoryUtility.AllInventoryItems(caravan).Any(thing => thing?.def?.category == ThingCategory.Item);
    internal static bool OwnsPawn(Caravan caravan, Pawn? pawn)
        => IsLivingOwner(caravan, pawn) && caravan.ContainsPawn(pawn!);
    private static bool IsLivingOwner(Caravan caravan, Pawn? pawn)
        => pawn is not null && !pawn.Destroyed && !pawn.Dead && caravan.IsOwner(pawn);
    internal static Pawn[] OwnedPawns(Caravan caravan) => caravan.PawnsListForReading.Where(pawn => IsLivingOwner(caravan, pawn)).ToArray();
    internal static bool HasOwnedPawn(Caravan caravan) => caravan.PawnsListForReading.Any(pawn => IsLivingOwner(caravan, pawn));
    internal static IReadOnlyList<ModWorldMapMarkerDto> FindTargets(ClientWorldMarkers markers, Caravan caravan, ClientColonyTargetKind kind)
    {
        var tile = caravan.Tile;
        return markers.FindTargets(tile.tileId, ClientWorldRuntimeValues.ReadPlanetTileLayerId(tile), kind);
    }
}
