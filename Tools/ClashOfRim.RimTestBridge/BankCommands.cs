using RimTest.Runtime;
using RimWorld.Planet;
using AIRsLight.ClashOfRim;
using static ClashOfRim.RimTestBridge.BridgeReflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Mercenaries;
using RimTest.Protocol;
using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed class BankCommands
{
    internal BankCommands(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy, Func<ModBankStatusResponseDto?> bankStatus)
    {
        router.Register("clash.bank.quests", "Observe persisted bank quest accounting and penalty clocks independently of network DTOs.", "observe", _ => BankQuests());
        router.Register("clash.bank.debts", "References to debt records currently displayed by the bank, for its original repay action.", "observe", a =>
            RimTest.Runtime.ObjectInspector.Paginate((bankStatus()?.OpenDebts ?? new()).Select(objects.Record).ToArray(), a));
        router.Register("clash.fixture.bankDeadline", "FIXTURE: shorten one existing unpaid quest's local deadline/next penalty to 10 ticks; leaves fees, stages, interval, ledger and punishment execution unchanged.", "fixture", a =>
        {
            policy.RequireFixture(a); string id = a.String("id"); long next = Find.TickManager.TicksGame + 10;
            var part = Find.QuestManager.QuestsListForReading.SelectMany(q => q.PartsListForReading).Single(p =>
                p is QuestPart_ClashBankLoan l && l.LoanId == id && !l.Completed || p is QuestPart_ClashBankDebt d && d.DebtId == id && !d.Completed);
            if (part is QuestPart_ClashBankLoan loan) { loan.DueAtGameTicks = next; loan.NextPenaltyGameTicks = next; }
            if (part is QuestPart_ClashBankDebt debt) { debt.DueAtGameTicks = next; debt.NextPenaltyGameTicks = next; }
            return new { fixture = true, id, next };
        });
        router.Register("clash.fixture.mercenaryDeath", "FIXTURE: kill the pawn of one active hired contract through Pawn.Kill; the real death notification creates any debt.", "fixture", a =>
        {
            policy.RequireFixture(a); var p = Find.QuestManager.QuestsListForReading.SelectMany(q => q.PartsListForReading.OfType<QuestPart_ClashMercenary>())
                .Single(p => p.ContractId == a.String("contractId") && !p.Completed && p.Pawn?.Spawned == true);
            p.Pawn!.Kill(null); return new { fixture = true, p.ContractId, p.DeathReported, dead = p.Pawn.Dead };
        });
        router.Register("clash.fixture.bankCash", "FIXTURE: replace spawned home silver with an explicit 0..20000 balance to test insufficient/exact payment; does not change bank records.", "fixture", a =>
        {
            policy.RequireFixture(a); int amount = a.Int("amount"); if (amount < 0 || amount > 20000) throw new CommandException("invalid_arguments", "amount: 0..20000");
            var map = BridgeMaps.Select(a); var cell = map.mapPawns.FreeColonistsSpawned.First().Position;
            foreach (var t in map.listerThings.ThingsOfDef(ThingDefOf.Silver).ToArray()) t.Destroy();
            int remaining = amount;
            while (remaining > 0)
            {
                var silver = ThingMaker.MakeThing(ThingDefOf.Silver); silver.stackCount = Math.Min(remaining, silver.def.stackLimit);
                remaining -= silver.stackCount;
                if (!GenPlace.TryPlaceThing(silver, cell, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Cannot place fixture cash.");
            }
            return new { fixture = true, amount };
        });
    }
    private static object BankQuests() => new
    {
        loans = Find.QuestManager.QuestsListForReading.SelectMany(q => q.PartsListForReading.OfType<QuestPart_ClashBankLoan>()).Select(p => new
        { p.LoanId, p.PrincipalSilver, p.InterestSilver, p.LateFeeSilver, p.TotalDueSilver, p.AnnualInterestRate, p.DueAtGameTicks, p.PenaltyIntervalTicks,
            p.TriggeredPenaltyCount, p.NextPenaltyGameTicks, p.Completed, p.DueWarningSent, stages = p.OverduePenaltyStages.Select(s => new { s.TriggerPenaltyCount, s.Kind, s.Severity }).ToArray() }).ToArray(),
        debts = Find.QuestManager.QuestsListForReading.SelectMany(q => q.PartsListForReading.OfType<QuestPart_ClashBankDebt>()).Select(p => new
        { p.DebtId, p.AmountSilver, p.SourceKind, p.Reason, p.DueAtGameTicks, p.PenaltyIntervalTicks, p.TriggeredPenaltyCount, p.NextPenaltyGameTicks, p.Completed, p.DueWarningSent,
            stages = p.OverduePenaltyStages.Select(s => new { s.TriggerPenaltyCount, s.Kind, s.Severity }).ToArray() }).ToArray(),
        mechanoids = Find.Maps.Sum(m => m.mapPawns.AllPawnsSpawned.Count(p => p.Faction == Faction.OfMechanoids)),
        raids = BankRaidObservation.Copy(),
        ticks = Find.TickManager.TicksGame
    };
}
