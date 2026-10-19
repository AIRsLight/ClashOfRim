using AIRsLight.ClashOfRim.Mercenaries;
using RimTest.Protocol;
using RimTest.Runtime;
using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed class MercenaryCommands
{
    internal MercenaryCommands(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy)
    {
        router.Register("clash.mercenaries", "Observe original mercenary quest contracts and current pawn/recall state.", "observe", a =>
            ObjectInspector.Paginate(Find.QuestManager.QuestsListForReading.SelectMany(q => q.PartsListForReading.OfType<QuestPart_ClashMercenary>()).Select(p => new
            {
                reference = objects.Add(p),
                p.ContractId,
                p.SkillDefName,
                p.SkillLevel,
                p.PriceSilver,
                p.ExpiresAtGameTicks,
                p.Completed,
                p.RecallStarted,
                p.RecallShuttleWaiting,
                p.RecallShuttleLoaded,
                pawn = p.Pawn is null ? null : objects.Add(p.Pawn),
                spawned = p.Pawn?.Spawned == true,
                actualSkill = DefDatabase<SkillDef>.GetNamedSilentFail(p.SkillDefName) is SkillDef skill ? p.Pawn?.skills?.GetSkill(skill)?.Level : null,
                shuttle = p.RecallShuttle is null ? null : objects.Add(p.RecallShuttle)
            }).ToArray(), a));
        router.Register("clash.mercenary.recall", "Use the original quest recall action on an active contract.", "semantic", a =>
        {
            policy.RequireLoopback(); var part = objects.Get<QuestPart_ClashMercenary>(a.String("contract"));
            if (!Find.QuestManager.QuestsListForReading.Any(q => q.PartsListForReading.Contains(part)) || !part.CanStartRecallNow)
                throw new CommandException("disabled", "Mercenary cannot be recalled now.");
            return new { accepted = part.TryStartRecallNow() };
        });
    }
}
