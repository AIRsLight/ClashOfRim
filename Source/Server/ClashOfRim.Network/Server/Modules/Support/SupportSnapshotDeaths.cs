using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using static AIRsLight.ClashOfRim.Network.Modules.Support.SupportReturns;
using static AIRsLight.ClashOfRim.Network.Modules.Support.SupportClosures;

namespace AIRsLight.ClashOfRim.Network.Modules.Support;

// Snapshot reconciliation owns one atomic batch for all detected support losses.
internal sealed class SupportSnapshotDeaths(IAuthoritativeEventLedger ledger, OnlinePresenceRegistry presence,
    EventNotificationHub notifications, SupportClosures closures, SnapshotMutationCoordinator mutations, SnapshotCommitService commits)
{
    internal void Process(SnapshotUploadResult upload)
    {
        using (mutations.EnterUnrecovered())
        {
            if (!commits.IsApplying) { using var recovered = mutations.Enter(); }
            ProcessLocked(upload);
        }
    }

    private void ProcessLocked(SnapshotUploadResult upload)
    {
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return;
        }

        SnapshotIdentity identity = upload.AcceptedSnapshot.Identity;
        if (string.IsNullOrWhiteSpace(identity.OwnerId) || string.IsNullOrWhiteSpace(identity.ColonyId))
        {
            return;
        }

        var losses = new List<AuthoritativeEvent>();
        var statuses = new List<LedgerStatusChange>();
        var notifyUsers = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<AuthoritativeEvent> allEvents = ledger.ListAll();
        foreach (AuthoritativeEvent supportEvent in ledger.ListForUser(identity.OwnerId!)
            .Where(evt => evt.Type == ServerEventType.SupportPawn)
            .Where(evt => evt.Status is ServerEventStatus.PendingOfflineDelivery
                or ServerEventStatus.ReadyForImmediateDelivery
                or ServerEventStatus.DeliveredToClient
                or ServerEventStatus.Failed)
            .Where(evt => EventVisibility.MatchesParty(evt.Target, identity.OwnerId!, identity.ColonyId!)))
        {
            if (supportEvent.Payload is not SupportPawnEventPayload payload
                || payload.ReturnToSender
                || !payload.TemporaryControl)
            {
                continue;
            }

            string? localId = ExtractPawnLocalId(payload.PawnGlobalKey);
            if (string.IsNullOrWhiteSpace(localId))
            {
                continue;
            }

            PawnSummary? pawn = upload.AcceptedSnapshot.Index.Pawns.FirstOrDefault(candidate =>
                string.Equals(candidate.LocalId, localId, StringComparison.Ordinal)
                || candidate.GlobalKey.EndsWith("/pawn:" + localId, StringComparison.Ordinal));
            if (pawn?.Dead != true)
            {
                continue;
            }

            SupportClosure? closure = closures.Find(supportEvent);
            if (closure is not null && (closure.Kind != SupportClosureKind.Loss
                || ((SupportPawnEventPayload)closure.Event.Payload).ReturnReason != "SnapshotDeath")) continue;
            AuthoritativeEvent? existing = closure?.Event;
            if (existing is null && HasSupportFinishEvent(supportEvent, allEvents)) continue;
            // A failed source alone is not evidence of snapshot death. Only replay
            // an existing loss; otherwise leave unrelated terminal events alone.
            if (supportEvent.Status == ServerEventStatus.Failed && existing is null)
            {
                continue;
            }

            AuthoritativeEvent lossEvent = CreateSupportPawnLossReturnEvent(
                supportEvent,
                payload,
                existing?.Payload is SupportPawnEventPayload savedLoss ? savedLoss.PawnName : pawn.Name ?? payload.PawnName,
                "SnapshotDeath",
                presence.IsUserOnline(supportEvent.Actor.UserId),
                existing?.CreatedAtUtc ?? upload.AcceptedSnapshot.AcceptedAtUtc);
            // A pre-upgrade loss may already be consumed. Reuse its persisted identity on recovery.
            if (existing is not null) lossEvent = lossEvent with { EventId = existing.EventId, IdempotencyKey = existing.IdempotencyKey };
            losses.Add(lossEvent);
            statuses.Add(new LedgerStatusChange(supportEvent.EventId, supportEvent.Status, ServerEventStatus.Failed, payload, payload));
            notifyUsers.Add(supportEvent.Actor.UserId);
        }

        if (losses.Count > 0)
        {
            // Validate existing loss ownership/payload and commit the whole death
            // batch before waking owners. Replay preserves consumed loss events.
            ledger.ApplyBatch(new LedgerBatch(losses, statuses));
            notifications.SignalUsers(notifyUsers);
        }
    }

    private static string? ExtractPawnLocalId(string globalKey)
    {
        const string marker = "/pawn:";
        int index = globalKey.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        string value = globalKey.Substring(index + marker.Length);
        int slash = value.IndexOf('/');
        return slash >= 0 ? value.Substring(0, slash) : value;
    }

}
