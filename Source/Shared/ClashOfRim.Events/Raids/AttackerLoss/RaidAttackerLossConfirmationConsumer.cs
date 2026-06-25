using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public sealed class RaidAttackerLossConfirmationConsumer
{
    private readonly IAuthoritativeEventLedger ledger;

    public RaidAttackerLossConfirmationConsumer(IAuthoritativeEventLedger ledger)
    {
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    public RaidAttackerLossConfirmationResult Consume(
        RaidAttackerLossConfirmationRequest request,
        DateTimeOffset confirmedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ConfirmedSnapshot);

        AuthoritativeEvent? ledgerEvent = ledger.Find(request.AttackerLossEventId);
        if (ledgerEvent == null)
        {
            return new RaidAttackerLossConfirmationResult(
                RaidAttackerLossConfirmationResultKind.EventNotFound,
                Event: null,
                AppliedSnapshotId: null,
                FailureReason: ServerLocalization.Text("Raid.AttackerLossEventNotFound"));
        }

        if (ledgerEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return new RaidAttackerLossConfirmationResult(
                RaidAttackerLossConfirmationResultKind.AlreadyApplied,
                ledgerEvent,
                ledgerEvent.AppliedSnapshotId,
                FailureReason: null);
        }

        if (ledgerEvent.Payload is not RaidEventPayload raidPayload || raidPayload.AttackerLoss == null)
        {
            return new RaidAttackerLossConfirmationResult(
                RaidAttackerLossConfirmationResultKind.NotAttackerLossEvent,
                ledgerEvent,
                AppliedSnapshotId: null,
                FailureReason: ServerLocalization.Text("Raid.AttackerLossNotEvent"));
        }

        RaidAttackerLossRecord loss = raidPayload.AttackerLoss;
        if (!string.Equals(request.SourceRaidEventId, loss.SourceRaidEventId, StringComparison.Ordinal))
        {
            AuthoritativeEvent changed = ledger.ReportApplicationResult(
                ledgerEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                ServerLocalization.Text("Raid.AttackerLossSourceMismatch"),
                nextRetryAtUtc: null);
            return Rejected(
                RaidAttackerLossConfirmationResultKind.SourceRaidMismatch,
                changed,
                ServerLocalization.Text("Raid.AttackerLossSourceMismatch"));
        }

        SnapshotIdentity confirmedIdentity = request.ConfirmedSnapshot.Identity;
        if (!IdentityMatches(request, ledgerEvent, confirmedIdentity))
        {
            AuthoritativeEvent changed = ledger.ReportApplicationResult(
                ledgerEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                ServerLocalization.Text("Raid.AttackerLossSnapshotIdentityMismatch"),
                nextRetryAtUtc: null);
            return Rejected(
                RaidAttackerLossConfirmationResultKind.SnapshotIdentityMismatch,
                changed,
                ServerLocalization.Text("Raid.AttackerLossSnapshotIdentityMismatch"));
        }

        if (!SnapshotBaseMatches(request, ledgerEvent, loss))
        {
            AuthoritativeEvent changed = ledger.ReportApplicationResult(
                ledgerEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                ServerLocalization.Text("Raid.AttackerLossSnapshotBaseMismatch"),
                nextRetryAtUtc: null);
            return Rejected(
                RaidAttackerLossConfirmationResultKind.SnapshotBaseMismatch,
                changed,
                ServerLocalization.Text("Raid.AttackerLossSnapshotBaseMismatch"));
        }

        IReadOnlyList<string> remainingKeys = FindRemainingLossKeys(loss, request.ConfirmedSnapshot.Index);
        if (remainingKeys.Count > 0)
        {
            AuthoritativeEvent changed = ledger.ReportApplicationResult(
                ledgerEvent.EventId,
                EventApplicationResultKind.LossNotReflected,
                ServerLocalization.Text(
                    "Raid.AttackerLossNotReflected",
                    new Dictionary<string, string?> { ["KEYS"] = string.Join(", ", remainingKeys) }),
                nextRetryAtUtc: null);
            return Rejected(
                RaidAttackerLossConfirmationResultKind.LossNotReflected,
                changed,
                ServerLocalization.Text(
                    "Raid.AttackerLossNotReflected",
                    new Dictionary<string, string?> { ["KEYS"] = string.Join(", ", remainingKeys) }));
        }

        AuthoritativeEvent applied = ledger.MarkApplied(
            ledgerEvent.EventId,
            confirmedIdentity.SnapshotId!,
            confirmedAtUtc);
        AuthoritativeEvent reported = ledger.ReportApplicationResult(
            ledgerEvent.EventId,
            EventApplicationResultKind.Applied,
            failureReason: null,
            nextRetryAtUtc: null);

        return new RaidAttackerLossConfirmationResult(
            RaidAttackerLossConfirmationResultKind.Accepted,
            reported,
            applied.AppliedSnapshotId,
            FailureReason: null);
    }

    private static bool IdentityMatches(
        RaidAttackerLossConfirmationRequest request,
        AuthoritativeEvent ledgerEvent,
        SnapshotIdentity confirmedIdentity)
    {
        return string.Equals(request.OwnerId, ledgerEvent.Target.UserId, StringComparison.Ordinal)
            && string.Equals(request.ColonyId, ledgerEvent.Target.ColonyId, StringComparison.Ordinal)
            && string.Equals(confirmedIdentity.OwnerId, request.OwnerId, StringComparison.Ordinal)
            && string.Equals(confirmedIdentity.ColonyId, request.ColonyId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(confirmedIdentity.SnapshotId);
    }

    private static bool SnapshotBaseMatches(
        RaidAttackerLossConfirmationRequest request,
        AuthoritativeEvent ledgerEvent,
        RaidAttackerLossRecord loss)
    {
        if (!string.Equals(request.AttackerSnapshotId, loss.AttackerSnapshotId, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(ledgerEvent.DeliveredToSnapshotId, loss.AttackerSnapshotId, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> FindRemainingLossKeys(
        RaidAttackerLossRecord loss,
        SaveSnapshotIndex confirmedIndex)
    {
        HashSet<string> presentLocalKeys = BuildPresentLocalKeys(confirmedIndex);
        List<string> remaining = new();

        foreach (string globalKey in loss.LostPawnGlobalKeys)
        {
            if (ContainsAnyLocalKey(presentLocalKeys, globalKey))
            {
                remaining.Add(globalKey);
            }
        }

        foreach (EventThingReference thing in loss.LostThings)
        {
            if (ContainsAnyLocalKey(presentLocalKeys, thing.GlobalKey))
            {
                remaining.Add(thing.GlobalKey);
            }
        }

        return remaining;
    }

    private static HashSet<string> BuildPresentLocalKeys(SaveSnapshotIndex index)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (ThingSummary thing in index.Things)
        {
            AddLocalKey(keys, thing.LocalId);
            AddLocalKey(keys, thing.GlobalKey);
        }

        foreach (PawnSummary pawn in index.Pawns.Where(pawn => !string.IsNullOrWhiteSpace(pawn.MapUniqueId)))
        {
            AddLocalKey(keys, pawn.LocalId);
            AddLocalKey(keys, pawn.GlobalKey);
        }

        return keys;
    }

    private static bool ContainsAnyLocalKey(HashSet<string> presentLocalKeys, string globalKey)
    {
        foreach (string key in ExpandLocalKeys(globalKey))
        {
            if (presentLocalKeys.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ExpandLocalKeys(string globalKey)
    {
        if (string.IsNullOrWhiteSpace(globalKey))
        {
            yield break;
        }

        yield return globalKey;

        int thingMarker = globalKey.LastIndexOf("/thing:", StringComparison.Ordinal);
        if (thingMarker >= 0)
        {
            string localId = globalKey.Substring(thingMarker + "/thing:".Length);
            yield return localId;
            yield return "Thing_" + localId;
            yield break;
        }

        int looseMarker = globalKey.LastIndexOf("thing:", StringComparison.Ordinal);
        if (looseMarker >= 0)
        {
            string localId = globalKey.Substring(looseMarker + "thing:".Length);
            yield return localId;
            yield return "Thing_" + localId;
        }
    }

    private static void AddLocalKey(HashSet<string> keys, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        keys.Add(key);
        if (key.StartsWith("Thing_", StringComparison.Ordinal))
        {
            keys.Add(key.Substring("Thing_".Length));
        }
        else
        {
            keys.Add("Thing_" + key);
        }
    }

    private static RaidAttackerLossConfirmationResult Rejected(
        RaidAttackerLossConfirmationResultKind kind,
        AuthoritativeEvent ledgerEvent,
        string failureReason)
    {
        return new RaidAttackerLossConfirmationResult(
            kind,
            ledgerEvent,
            AppliedSnapshotId: null,
            failureReason);
    }
}
