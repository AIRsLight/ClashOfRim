using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Events;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class SnapshotConfirmationPlanner(IAuthoritativeEventLedger ledger)
{
    internal SnapshotConfirmationPlan Plan(SnapshotConfirmationMutation mutation, LatestSnapshotRecord snapshot)
    {
        mutation.Preflight(ledger);
        var copy = new InMemoryAuthoritativeEventLedger();
        var sources = mutation.Sources.Select(item => ledger.Find(item.EventId)).Where(item => item is not null).Cast<AuthoritativeEvent>().ToArray();
        foreach (var source in sources) copy.Append(source);
        var achievements = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);
        var results = mutation.Request.Applications.Select(application => EventApplicationConfirmation.Evaluate(application,
            snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!, mutation.Request.BaseSnapshotId, snapshot, copy,
            snapshot.AcceptedAtUtc, item => achievements[item.EventId] = item)).ToArray();
        var plan = new SnapshotConfirmationPlan(results, sources.Select(source => new LedgerConfirmationChange(source, copy.Find(source.EventId)!)).ToArray(),
            achievements.Values.ToArray());
        (mutation with { Plan = plan }).Validate(snapshot);
        return plan;
    }
}
