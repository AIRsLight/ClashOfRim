using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Raids;
using RimTest.Protocol;
using RimTest.Runtime;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace ClashOfRim.RimTestBridge;

internal sealed class CombatCommands
{
    internal CombatCommands(CommandRouter router, BridgeActionPolicy policy)
    {
        router.Register("clash.combat", "Observe defense assignments, native pawn jobs, lords and faction relations on the selected map.", "observe", a =>
        {
            var map = a.Bool("remote") ? Find.Maps.Single(m => "Map_" + m.uniqueID == ClashOfRimGameComponent.ActiveRemoteMapSession?.ClientMapId) : BridgeMaps.Select(a);
            var owner = map.Parent.Faction;
            return new
            {
                mapId = "Map_" + map.uniqueID, owner = owner?.GetUniqueLoadID(), player = Faction.OfPlayer.GetUniqueLoadID(),
                pawns = map.mapPawns.AllPawnsSpawned.Where(p => CombatFixture.IsTagged(p) || p.Faction == Faction.OfPlayer).Select(p => new
                {
                    name = p.Name?.ToStringFull, id = p.ThingID, faction = p.Faction?.GetUniqueLoadID(), factionDef = p.Faction?.def.defName,
                    player = p.Faction == Faction.OfPlayer, p.Dead, p.Downed, p.IsColonistPlayerControlled,
                    ownerRelation = Relation(p.Faction, owner), playerRelation = Relation(p.Faction, Faction.OfPlayer),
                    lord = p.GetLord()?.LordJob.GetType().Name, duty = p.mindState?.duty?.def.defName,
                    focus = p.mindState?.duty?.focus.Cell.ToString(), radius = p.mindState?.duty?.radius,
                    wanderRadius = p.mindState?.duty?.wanderRadius, position = p.Position.ToString(),
                    x = p.Position.x, z = p.Position.z, job = p.CurJob?.def.defName,
                    target = p.CurJob?.targetA.Thing?.ThingID,
                    targetFaction = p.CurJob?.targetA.Thing?.Faction?.GetUniqueLoadID(),
                    shotsFired = p.records?.GetAsInt(RecordDefOf.ShotsFired),
                    enemy = p.mindState?.enemyTarget?.ThingID,
                    distanceToFocus = p.mindState?.duty?.focus.IsValid == true ? p.Position.DistanceTo(p.mindState.duty.focus.Cell) : (float?)null
                }).OrderBy(p => p.name, StringComparer.Ordinal).ToArray(),
                points = map.listerThings.AllThings.OfType<Building_ClashDefensePoint>().Select(p => new
                {
                    defName = p.def.defName, mode = p.AiMode.ToString(), radius = p.ActionRadius, position = p.Position.ToString(),
                    pawn = p.AssignedPawn?.Name?.ToStringFull, assignedCount = p.GetComp<CompAssignableToPawn>().AssignedPawnsForReading.Count
                }).OrderBy(p => p.pawn, StringComparer.Ordinal).ToArray(),
                factions = Find.FactionManager.AllFactionsListForReading.Where(f => !f.IsPlayer && !f.temporary)
                    .OrderBy(f => f.GetUniqueLoadID(), StringComparer.Ordinal)
                    .Select(f => new { id = f.GetUniqueLoadID(), defName = f.def.defName, playerRelation = Relation(f, Faction.OfPlayer) }).ToArray()
            };
        });
        router.Register("clash.fixture.combat", "FIXTURE: create assigned defenders, a manned mortar and friendly/hostile NPCs with native lords on the home map.", "fixture", a =>
        {
            policy.RequireFixture(a);
            CombatFixture.Create(BridgeMaps.Select(a));
            return new { fixture = true };
        });
        router.Register("clash.fixture.combatTarget", "FIXTURE: move the existing hostile NPC into the hold defender's firing range on an active raid map.", "fixture", a =>
        {
            policy.RequireFixture(a);
            var session = ClashOfRimGameComponent.ActiveRemoteMapSession;
            if (session?.IsRaidBattle != true) throw new CommandException("invalid_context", "Requires an active raid.");
            var map = Find.Maps.Single(m => "Map_" + m.uniqueID == session.ClientMapId);
            var hold = map.mapPawns.AllPawnsSpawned.Single(p => p.Name?.ToStringFull == "RimTestCombat Hold");
            var raider = map.mapPawns.AllPawnsSpawned.Single(p => p.Name?.ToStringFull == "RimTestCombat Raider");
            raider.Position = hold.mindState.duty.focus.Cell + new IntVec3(-19, 0, 0);
            raider.Notify_Teleported();
            return new { fixture = true, target = raider.ThingID };
        });
    }
    private static string? Relation(Faction? a, Faction? b) => a is null || b is null ? null : a == b ? "Self" : a.RelationKindWith(b).ToString();
}
