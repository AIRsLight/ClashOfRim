using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimTest.Protocol;
using RimTest.Runtime;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace ClashOfRim.RimTestBridge;

internal sealed class RemoteMapCommands
{
    internal RemoteMapCommands(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy)
    {
        router.Register("clash.remote", "Read active observation/raid identity and object references.", "observe", _ =>
        {
            var session = ClashOfRimGameComponent.ActiveRemoteMapSession;
            return new
            {
                active = session?.IsActive == true,
                session = session is null ? null : new
                {
                    reference = objects.Add(session),
                    session.SessionId,
                    kind = session.Kind.ToString(),
                    session.Mode,
                    session.TargetUserId,
                    session.TargetSnapshotId,
                    session.TargetMapId,
                    session.ClientMapId,
                    session.RelatedEventId,
                    session.IsRaidBattle,
                    session.IsObservation
                },
                raid = ClashOfRimGameComponent.ActiveRaidBattleSession is { } raid ? objects.Add(raid) : null
            };
        });
        router.Register("clash.remote.rules", "Observe player-proxy diplomacy, remote resident duties, trap concealment and fog at a requested cell.", "observe", a =>
        {
            var session = ClashOfRimGameComponent.ActiveRemoteMapSession;
            string? userId = a.OptionalString("userId") ?? session?.TargetUserId;
            // Do not normalize/create proxies in an observation command.
            var owner = Find.FactionManager?.AllFactionsListForReading.SingleOrDefault(f =>
                PlayerFactionProxyUtility.IsServerPlayerProxy(f) && PlayerFactionProxyUtility.ProxyOwnerUserId(f) == userId);
            var map = Find.Maps?.SingleOrDefault(m => "Map_" + m.uniqueID == session?.ClientMapId);
            var probe = new IntVec3(a.Int("x"), 0, a.Int("z"));
            if (map is not null && !probe.InBounds(map)) throw new CommandException("invalid_argument", "Fog probe is outside the remote map.");
            return new
            {
                active = session?.IsActive == true,
                diplomacy = owner is null ? null : new
                {
                    ownerToPlayer = owner.RelationKindWith(Faction.OfPlayer).ToString(),
                    playerToOwner = Faction.OfPlayer.RelationKindWith(owner).ToString()
                },
                residents = map?.mapPawns.AllPawnsSpawned.Where(p => p.Faction == owner && p.RaceProps.Humanlike).Select(p => new
                {
                    id = p.ThingID, playerRelation = p.Faction.RelationKindWith(Faction.OfPlayer).ToString(),
                    p.IsColonistPlayerControlled, lord = p.GetLord()?.LordJob.GetType().Name,
                    duty = p.mindState?.duty?.def.defName, job = p.CurJob?.def.defName,
                    enemyIsPlayer = p.mindState?.enemyTarget?.Faction == Faction.OfPlayer
                }).ToArray(),
                concealment = map is null ? null : new
                {
                    visibleTraps = map.listerThings.AllThings.OfType<Building_Trap>().Count(),
                    hiddenTraps = map.listerThings.AllThings.OfType<Building_ClashHiddenTrapProxy>().Count(p => p.HasOriginalTrap),
                    probeVisibleTrap = map.listerThings.AllThings.OfType<Building_Trap>().Any(p => p.Position == probe),
                    probeHiddenTrap = map.listerThings.AllThings.OfType<Building_ClashHiddenTrapProxy>().Any(p => p.Position == probe && p.HasOriginalTrap),
                    probeFogged = probe.Fogged(map),
                    edgeRevealed = map.BoundsRect(0).EdgeCells.Any(c => c.Standable(map) && !c.Roofed(map) && !c.Fogged(map))
                }
            };
        });
        router.Register("clash.remote.close", "Original remote-map primary action, with raid confirmation enabled.", "semantic", _ =>
        {
            policy.RequireLoopback();
            return new { invoked = RemoteMapSessionController.TryRunPrimaryAction(ClashOfRimGameComponent.ActiveRemoteMapSession, "RimTest player finish", true) };
        });
        router.Register("clash.fixture.observation", "FIXTURE: create one trap and a sealed roofed room for observation concealment checks.", "fixture", a =>
        {
            policy.RequireFixture(a);
            var map = BridgeMaps.Select(a);
            var center = map.Center + new IntVec3(20, 0, 20);
            var room = CellRect.CenteredOn(center, 2);
            foreach (var cell in room.Cells)
            {
                foreach (var thing in cell.GetThingList(map).ToArray())
                    if (thing.def.category != ThingCategory.Pawn && thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                map.fogGrid.Unfog(cell);
            }
            foreach (var cell in room.EdgeCells)
            {
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                wall.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(wall, cell, map);
            }
            var trap = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TrapSpike"), ThingDefOf.Steel);
            trap.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(trap, center, map);
            return new { fixture = true, trap = trap.ThingID, x = center.x, z = center.z };
        });
    }
}
