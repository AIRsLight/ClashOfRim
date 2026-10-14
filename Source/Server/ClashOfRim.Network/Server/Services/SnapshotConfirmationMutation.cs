using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed record SnapshotConfirmationRequest(string BaseSnapshotId, IReadOnlyList<ConfirmEventApplicationEntry> Applications, bool Single)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseSnapshotId) || Applications is null || Single && Applications.Count != 1
            || Applications.Any(item => item is null || string.IsNullOrWhiteSpace(item.EventId) || item.ClientApplicationResult is null))
            throw new InvalidDataException("Invalid snapshot confirmation request.");
    }
    internal bool Matches(SnapshotConfirmationRequest other) => JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);
}

public sealed record SnapshotConfirmationSource(string EventId, AuthoritativeEvent? Expected);
public sealed record SnapshotConfirmationPlan(IReadOnlyList<ConfirmEventApplicationResultDto> Results,
    IReadOnlyList<LedgerConfirmationChange> Changes, IReadOnlyList<AuthoritativeEvent> AchievementEvents)
{
    internal LedgerBatch Batch => new([], [], ConfirmationChanges: Changes);
}

/// <summary>Original inputs survive publication; decisions are checkpointed after post-processing and before ledger writes.</summary>
public sealed record SnapshotConfirmationMutation(SnapshotConfirmationRequest Request,
    IReadOnlyList<SnapshotConfirmationSource> Sources, SnapshotConfirmationPlan? Plan = null)
{
    internal static SnapshotConfirmationMutation Capture(SnapshotConfirmationRequest request, IAuthoritativeEventLedger ledger)
    {
        request.Validate();
        return new(request, request.Applications.Select(item => item.EventId).Distinct(StringComparer.Ordinal)
            .Select(id => new SnapshotConfirmationSource(id, ledger.Find(id))).ToArray());
    }

    internal void Validate(LatestSnapshotRecord? snapshot = null)
    {
        Request.Validate();
        var ids = Request.Applications.Select(item => item.EventId).Distinct(StringComparer.Ordinal).ToArray();
        if (Sources is null || Sources.Any(item => item is null || item.Expected is not null && item.Expected.EventId != item.EventId)
            || !ids.SequenceEqual(Sources.Select(item => item.EventId)))
            throw new InvalidDataException("Confirmation sources do not match the original entries.");
        if (Plan is not { } plan) return;
        if (plan.Results is null || plan.Changes is null || plan.AchievementEvents is null
            || plan.Results.Any(item => item is null || item.Result is null)
            || plan.Changes.Any(item => item is null || item.Expected is null || item.Updated is null)
            || !Request.Applications.Select(item => item.EventId).SequenceEqual(plan.Results.Select(item => item.EventId))
            || !Sources.Where(item => item.Expected is not null).Select(item => item.EventId).SequenceEqual(plan.Changes.Select(item => item.Expected.EventId)))
            throw new InvalidDataException("Confirmation decisions do not match the original entries.");
        var before = plan.Changes.ToDictionary(item => item.Expected.EventId, item => item.Expected, StringComparer.Ordinal);
        LedgerBatchValidation.Validate(plan.Batch, id => before.GetValueOrDefault(id), _ => null);
        foreach (var source in Sources.Where(item => item.Expected is not null))
            if (!SameSource(source.Expected!, before[source.EventId])) throw new InvalidDataException("Confirmation decision changed source ownership/content.");
        foreach (var change in plan.Changes.Where(change => !Same(change.Expected, change.Updated)))
        {
            var updated = change.Updated;
            bool applied = updated.Status == ServerEventStatus.AppliedToSnapshot;
            if (applied && change.Expected.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget)
                throw new InvalidDataException("Confirmation decision revives an ended event.");
            if (!applied && (updated.DeliveredToSnapshotId != change.Expected.DeliveredToSnapshotId || updated.DeliveredAtUtc != change.Expected.DeliveredAtUtc
                || updated.AppliedSnapshotId != change.Expected.AppliedSnapshotId || updated.AppliedAtUtc != change.Expected.AppliedAtUtc))
                throw new InvalidDataException("Rejected confirmation changed delivery/application attribution.");
            if (snapshot is not null)
            {
                var party = updated.Type == ServerEventType.Trade ? updated.Actor : updated.Target;
                if (party.UserId != snapshot.Identity.OwnerId || !string.IsNullOrWhiteSpace(party.ColonyId) && party.ColonyId != snapshot.Identity.ColonyId)
                    throw new InvalidDataException("Confirmation decision targets another colony.");
                if (applied
                    && (updated.AppliedSnapshotId != (change.Expected.Status == ServerEventStatus.AppliedToSnapshot
                            ? change.Expected.AppliedSnapshotId : snapshot.Identity.SnapshotId)
                        || updated.AppliedAtUtc != (change.Expected.Status == ServerEventStatus.AppliedToSnapshot
                            ? change.Expected.AppliedAtUtc : snapshot.AcceptedAtUtc)
                        || updated.LastApplicationResult != EventApplicationResultKind.Applied
                        || updated.LastFailureReason is not null || updated.NextRetryAtUtc is not null
                        || updated.DeliveredToSnapshotId != (string.IsNullOrWhiteSpace(change.Expected.DeliveredToSnapshotId)
                            ? Request.BaseSnapshotId : change.Expected.DeliveredToSnapshotId)
                        || updated.DeliveredAtUtc != (change.Expected.DeliveredAtUtc ?? updated.AppliedAtUtc)))
                    throw new InvalidDataException("Confirmation decision changed snapshot attribution.");
            }
        }
        foreach (var result in plan.Results.Where(item => item.Result.Accepted))
            if (!plan.Changes.Any(change => change.Updated.EventId == result.EventId
                && change.Updated.Status == ServerEventStatus.AppliedToSnapshot && change.Updated.AppliedSnapshotId == result.AppliedSnapshotId))
                throw new InvalidDataException("Confirmation success is not backed by an applied event.");
        foreach (var item in plan.AchievementEvents)
            if (item is null || !plan.Changes.Any(change => Same(change.Updated, item))
                || !plan.Results.Any(result => result.EventId == item.EventId && result.Result.Accepted)
                || item.Type != ServerEventType.Trade || item.Status != ServerEventStatus.AppliedToSnapshot
                || item.AppliedAtUtc is null || string.IsNullOrWhiteSpace(item.AppliedSnapshotId))
                throw new InvalidDataException("Invalid confirmation achievement source.");
    }

    internal void Preflight(IAuthoritativeEventLedger ledger)
    {
        foreach (var source in Sources)
        {
            var current = ledger.Find(source.EventId);
            if (source.Expected is null ? current is not null : current is null || Plan is null && !SameSource(source.Expected, current))
                throw new InvalidDataException("Confirmation source changed ownership/content since publication was prepared.");
        }
        if (Plan is { } plan) LedgerBatchValidation.Validate(plan.Batch, ledger.Find, ledger.FindByIdempotencyKey);
    }

    // Post-processors may close a support event or report a failure. They may not redirect its payload or delivery.
    private static bool SameSource(AuthoritativeEvent expected, AuthoritativeEvent current) => Same(expected with
    { Status = current.Status, LastApplicationResult = current.LastApplicationResult, LastFailureReason = current.LastFailureReason,
        NextRetryAtUtc = current.NextRetryAtUtc }, current);
    internal static bool Same(AuthoritativeEvent left, AuthoritativeEvent right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}

public sealed record SnapshotConfirmationReceipt(int Version, SaveSnapshotEnvelope Envelope, DateTimeOffset AcceptedAtUtc,
    SnapshotConfirmationRequest Request, IReadOnlyList<ConfirmEventApplicationResultDto> Results)
{
    internal void Validate()
    {
        Request.Validate();
        if (Version != 1 || string.IsNullOrWhiteSpace(Envelope.Identity.OwnerId) || string.IsNullOrWhiteSpace(Envelope.Identity.ColonyId)
            || string.IsNullOrWhiteSpace(Envelope.Identity.SnapshotId) || string.IsNullOrWhiteSpace(Envelope.NextLineageToken)
            || Results is null || Results.Any(item => item is null || item.Result is null)
            || !Request.Applications.Select(item => item.EventId).SequenceEqual(Results.Select(item => item.EventId)))
            throw new InvalidDataException("Invalid snapshot confirmation receipt.");
    }
}
