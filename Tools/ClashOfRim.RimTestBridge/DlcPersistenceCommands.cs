using RimTest.Runtime;
using RimWorld.Planet;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim;
using static ClashOfRim.RimTestBridge.BridgeReflection;
using RimTest.Protocol;
using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed class DlcPersistenceCommands
{
    internal DlcPersistenceCommands(CommandRouter router, BridgeActionPolicy policy)
    {
        router.Register("clash.dlc.pawns", "Read stable DLC pawn identities: royal title/honor, psylink/abilities, ideology/culture, genes and death-refusal uses.", "observe", _ => DlcPawnState());
        router.Register("clash.fixture.dlcPawn", "FIXTURE: give one colonist representative enabled-DLC state using game APIs. Does not emulate earning titles, surgery, rituals or resurrection.", "fixture", a =>
        {
            policy.RequireFixture(a); var p = BridgeMaps.Select(a).mapPawns.FreeColonistsSpawned.OrderBy(p => p.ThingID).First();
            if (ModsConfig.RoyaltyActive)
            {
                var empire = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Empire) ?? throw new InvalidOperationException("Empire missing.");
                p.royalty.SetFavor(empire, 6, false);
                p.royalty.SetTitle(empire, DefDatabase<RoyalTitleDef>.GetNamed("Yeoman"), false, false, false);
                p.ChangePsylinkLevel(1, false); p.abilities.GainAbility(DefDatabase<AbilityDef>.GetNamed("Painblock"));
            }
            if (ModsConfig.IdeologyActive && p.Ideo is null) throw new InvalidOperationException("Player ideology missing.");
            if (ModsConfig.BiotechActive) p.genes.AddGene(DefDatabase<GeneDef>.GetNamed("Robust"), true);
            if (ModsConfig.AnomalyActive)
            {
                var refusal = (Hediff_DeathRefusal)p.health.AddHediff(DefDatabase<HediffDef>.GetNamed("DeathRefusal"));
                refusal.SetUseAmountDirect(2);
            }
            return new { fixture = true, pawnId = p.ThingID, state = DlcPawnState() };
        });
    }
    private static object DlcPawnState() => Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).OrderBy(p => p.ThingID).Select(p => new
    {
        id = p.ThingID,
        titles = p.royalty?.AllTitlesForReading.Select(t => new { faction = t.faction.GetUniqueLoadID(), defName = t.def.defName, honor = p.royalty.GetFavor(t.faction) }).OrderBy(t => t.faction).ToArray(),
        psylink = p.GetPsylinkLevel(),
        abilities = p.abilities?.abilities.Select(a => a.def.defName).Order().ToArray(),
        ideology = p.Ideo?.GetUniqueLoadID(), culture = p.Ideo?.culture?.defName,
        genes = p.genes?.GenesListForReading.Select(g => new { defName = g.def.defName, xeno = p.genes.Xenogenes.Contains(g) }).OrderBy(g => g.defName).ToArray(),
        refusalUses = p.health.hediffSet.hediffs.OfType<Hediff_DeathRefusal>().Select(h => h.UsesLeft).ToArray()
    }).ToArray();
}
