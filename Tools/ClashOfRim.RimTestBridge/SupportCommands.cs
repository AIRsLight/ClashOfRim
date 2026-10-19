using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Support;
using RimTest.Protocol;
using RimTest.Runtime;
using Verse;
using static ClashOfRim.RimTestBridge.BridgeReflection;

namespace ClashOfRim.RimTestBridge;

internal sealed class SupportCommands
{
    internal SupportCommands(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy)
    {
        router.Register("clash.support.return", "Use the original quest early-return button, including completion tracking.", "semantic", a =>
        {
            policy.RequireLoopback(); string id = a.String("eventId");
            var part = Find.QuestManager.QuestsListForReading.SelectMany(q => q.PartsListForReading.OfType<QuestPart_ClashSupportPawn>())
                .SingleOrDefault(p => p.EventId == id && p.CanStartEarlyReturnNow);
            if (part is null) throw new CommandException("disabled", "No active quest accepts early return.");
            return new { accepted = part.TryStartEarlyReturnNow() };
        });
        router.Register("clash.support.assignments", "Read current received support assignments and their stable event/pawn identities.", "observe", a =>
        {
            var component = Current.Game?.GetComponent<ClashOfRimGameComponent>();
            var assignments = component is null ? Array.Empty<ActiveSupportPawnAssignment>() : component.CopyActiveSupportAssignments().ToArray();
            return ObjectInspector.Paginate(assignments.Select(s => new { s.EventId, s.PawnGlobalKey, s.PawnThingId, s.OwnerUserId, s.PermanentSupport, s.SupportDurationDays, s.FinishInProgress }).ToArray(), a);
        });
        router.Register("clash.support.confirmDuration", "Confirm the original support-duration dialog using its offered 3..30 day range or permanent mode.", "semantic", a =>
        {
            policy.RequireLoopback(); var w = objects.Get<Window>(a.String("window"));
            if (!Find.WindowStack.Windows.Contains(w) || w.GetType().FullName != "AIRsLight.ClashOfRim.Support.SupportDurationDialogWindow")
                throw new CommandException("invalid_context", "Expected current support duration dialog.");
            int days = a.Int("days", 7); bool permanent = a.Bool("permanent");
            if (days < 3 || days > 30) throw new CommandException("invalid_arguments", "days: 3..30.");
            var action = Field<Action<int?, bool>>(w, "confirmAction"); w.Close(); action(permanent ? null : days, permanent);
            return new { state = "invoked" };
        });
    }
}
