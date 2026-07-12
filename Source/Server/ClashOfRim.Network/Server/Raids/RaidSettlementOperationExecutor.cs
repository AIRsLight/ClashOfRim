using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public enum RaidSettlementOperationResultKind
{
    Completed,
    AlreadyCompleted,
    ManualReview
}

public sealed record RaidSettlementOperationResult(
    RaidSettlementOperationResultKind Kind,
    string? DefenderSnapshotId,
    string? SettlementEventId,
    string? Message);

public static class RaidSettlementOperationExecutor
{
    public static RaidSettlementOperationResult Execute(
        ClashOfRimNetworkState state,
        RaidSettlementDeferredPayload payload,
        SaveSnapshotPackage evidencePackage,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(evidencePackage);

        AuthoritativeEvent? sourceRaid = state.Ledger.Find(payload.RaidEventId);
        if (sourceRaid is null)
        {
            return ManualReview(state, payload.RaidEventId, "Raid settlement source event was not found.");
        }

        AuthoritativeEvent? existingSettlement = FindSettlementEvent(state.Ledger, payload.RaidEventId);
        if (sourceRaid.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return new RaidSettlementOperationResult(
                RaidSettlementOperationResultKind.AlreadyCompleted,
                sourceRaid.AppliedSnapshotId,
                existingSettlement?.EventId,
                null);
        }

        if (!TryValidateSource(sourceRaid, payload, out RaidEventPayload? raidPayload, out string? validationFailure))
        {
            return ManualReview(state, payload.RaidEventId, validationFailure!);
        }

        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
        {
            return ManualReview(state, payload.RaidEventId, "Raid settlement requires a snapshot package store.");
        }

        if (!IdentityMatchesEvidence(evidencePackage.Envelope.Identity, payload))
        {
            return ManualReview(state, payload.RaidEventId, "Raid settlement evidence identity does not match the scheduled operation.");
        }

        SaveSnapshotPackage? originalDefensePackage = packageStore.GetLatestPackage(
            payload.DefenderUserId,
            payload.DefenderColonyId);
        if (originalDefensePackage is null)
        {
            return ManualReview(state, payload.RaidEventId, "Raid defender snapshot package was not found.");
        }

        if (!string.Equals(
                originalDefensePackage.Envelope.Identity.SnapshotId,
                payload.DefenderSnapshotId,
                StringComparison.Ordinal))
        {
            if (existingSettlement is not null
                && string.Equals(
                    originalDefensePackage.Envelope.PreviousSnapshotId,
                    payload.DefenderSnapshotId,
                    StringComparison.Ordinal))
            {
                return CompletePreviouslyStoredSettlement(
                    state,
                    sourceRaid,
                    originalDefensePackage.Envelope.Identity.SnapshotId,
                    existingSettlement,
                    nowUtc);
            }

            return ManualReview(state, payload.RaidEventId, "Raid defender snapshot changed before settlement completed.");
        }

        string targetMapId = sourceRaid.TargetContext?.MapUniqueId ?? string.Empty;
        string evidenceMapId = ResolveEvidenceMapId(evidencePackage.Index, targetMapId, sourceRaid.EventId);
        RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(
            new RaidSettlementReturnRequest(
                sourceRaid.EventId,
                originalDefensePackage.Envelope.Identity,
                originalDefensePackage,
                evidencePackage,
                targetMapId,
                LossRatio: state.ServerConfiguration.RaidSettlementLossRatio,
                PackableBuildingDefNames: state.AdminBaseline.Current?.PackableBuildingDefNames,
                BuildingMaxHitPointsByDefName: state.AdminBaseline.Current?.EstimatedSettlementMaxHitPointsByDefName,
                StuffHitPointFactorByDefName: state.AdminBaseline.Current?.StuffHitPointFactorByDefName,
                StuffHitPointOffsetByDefName: state.AdminBaseline.Current?.StuffHitPointOffsetByDefName,
                MinimumRemainingHitPointsRatio: state.ServerConfiguration.RaidSettlementMinimumRemainingHitPointsRatio,
                IgnoredThingDefNames: state.Plugins.ActiveIgnoredRaidSettlementThingDefNames(state.CompatibilityBaseline.Current),
                ReturnedMapUniqueId: evidenceMapId,
                BuildingHitPointsLossRatio: state.ServerConfiguration.RaidSettlementBuildingHitPointsLossRatio,
                TrapDefNames: state.AdminBaseline.Current?.ApprovedTrapDefNames));
        if (!settlement.Accepted)
        {
            RaidSettlementLedgerRecordResult rejected = RaidSettlementLedgerRecorder.Record(
                state.Ledger,
                sourceRaid.EventId,
                settlement,
                nowUtc,
                state.OnlinePresence.IsUserOnline(sourceRaid.Target.UserId));
            return new RaidSettlementOperationResult(
                RaidSettlementOperationResultKind.ManualReview,
                null,
                rejected.SettlementEvent?.EventId,
                rejected.FailureReason ?? $"Raid settlement was rejected: {settlement.Kind}.");
        }

        string editedSnapshotId = BuildSettlementSnapshotId(payload.DefenderColonyId, sourceRaid.EventId);
        SaveSnapshotPackage editedPackage;
        try
        {
            editedPackage = RaidSettlementSnapshotEditor.ApplySettlementLosses(
                originalDefensePackage,
                settlement,
                editedSnapshotId,
                nowUtc,
                state.Plugins.ActiveRaidSettlementSnapshotEditorExtensions(state.CompatibilityBaseline.Current));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException)
        {
            return ManualReview(state, payload.RaidEventId, "Raid defender snapshot could not be edited: " + ex.Message);
        }

        if (!string.Equals(editedPackage.Envelope.Identity.OwnerId, payload.DefenderUserId, StringComparison.Ordinal)
            || !string.Equals(editedPackage.Envelope.Identity.ColonyId, payload.DefenderColonyId, StringComparison.Ordinal))
        {
            return ManualReview(state, payload.RaidEventId, "Edited raid defender snapshot identity does not match the scheduled operation.");
        }

        RaidSettlementLedgerRecordResult record;
        lock (state.RaidSettlementSnapshotMutationGate)
        {
            sourceRaid = state.Ledger.Find(payload.RaidEventId);
            existingSettlement = FindSettlementEvent(state.Ledger, payload.RaidEventId);
            if (sourceRaid?.Status == ServerEventStatus.AppliedToSnapshot)
            {
                return new RaidSettlementOperationResult(
                    RaidSettlementOperationResultKind.AlreadyCompleted,
                    sourceRaid.AppliedSnapshotId,
                    existingSettlement?.EventId,
                    null);
            }

            if (sourceRaid is null)
            {
                return ManualReview(state, payload.RaidEventId, "Raid settlement source event disappeared before commit.");
            }

            SaveSnapshotPackage? currentDefenderPackage = packageStore.GetLatestPackage(
                payload.DefenderUserId,
                payload.DefenderColonyId);
            if (currentDefenderPackage is null
                || !string.Equals(
                    currentDefenderPackage.Envelope.Identity.SnapshotId,
                    payload.DefenderSnapshotId,
                    StringComparison.Ordinal))
            {
                return ManualReview(state, payload.RaidEventId, "Raid defender snapshot changed before settlement commit.");
            }

            record = RaidSettlementLedgerRecorder.Record(
                state.Ledger,
                sourceRaid.EventId,
                settlement,
                nowUtc,
                state.OnlinePresence.IsUserOnline(sourceRaid.Target.UserId));
            if (record.Kind is not (RaidSettlementLedgerRecordResultKind.SettlementEventCreated
                or RaidSettlementLedgerRecordResultKind.SettlementEventAlreadyExists))
            {
                return new RaidSettlementOperationResult(
                    RaidSettlementOperationResultKind.ManualReview,
                    editedPackage.Envelope.Identity.SnapshotId,
                    record.SettlementEvent?.EventId,
                    record.FailureReason ?? $"Raid settlement ledger rejected result {record.Kind}.");
            }

            packageStore.StoreLatest(editedPackage, editedPackage.Index, nowUtc);
            state.Players.RecordLatestSnapshotReference(
                payload.DefenderUserId,
                payload.DefenderColonyId,
                editedPackage.Envelope.Identity.SnapshotId,
                nowUtc);

            if (string.IsNullOrWhiteSpace(sourceRaid.DeliveredToSnapshotId))
            {
                state.Ledger.MarkDelivered(
                    sourceRaid.EventId,
                    originalDefensePackage.Envelope.Identity.SnapshotId ?? raidPayload!.DefenderSnapshotId,
                    nowUtc);
            }

            state.Ledger.MarkApplied(sourceRaid.EventId, editedSnapshotId, nowUtc);
            state.Ledger.ReportApplicationResult(
                sourceRaid.EventId,
                EventApplicationResultKind.Applied,
                failureReason: null,
                nextRetryAtUtc: null);
        }

        if (record.SettlementEvent is not null)
        {
            state.EventNotifications.SignalUser(record.SettlementEvent.Target.UserId);
        }

        state.RuntimeLogger.LogInformation(
            "Completed deferred raid settlement: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId} defender={DefenderUserId}/{DefenderColonyId} evidence={EvidenceSnapshotId} defenderSnapshot={DefenderSnapshotId}",
            payload.RaidEventId,
            payload.AttackerUserId,
            payload.AttackerColonyId,
            payload.DefenderUserId,
            payload.DefenderColonyId,
            payload.EvidenceSnapshotId,
            editedSnapshotId);
        return new RaidSettlementOperationResult(
            RaidSettlementOperationResultKind.Completed,
            editedSnapshotId,
            record.SettlementEvent?.EventId,
            null);
    }

    private static RaidSettlementOperationResult CompletePreviouslyStoredSettlement(
        ClashOfRimNetworkState state,
        AuthoritativeEvent sourceRaid,
        string? defenderSnapshotId,
        AuthoritativeEvent settlementEvent,
        DateTimeOffset nowUtc)
    {
        string appliedSnapshotId = defenderSnapshotId ?? sourceRaid.AppliedSnapshotId ?? "raid-settlement";
        state.Players.RecordLatestSnapshotReference(
            sourceRaid.Target.UserId,
            sourceRaid.Target.ColonyId ?? string.Empty,
            appliedSnapshotId,
            nowUtc);
        if (string.IsNullOrWhiteSpace(sourceRaid.DeliveredToSnapshotId))
        {
            state.Ledger.MarkDelivered(
                sourceRaid.EventId,
                sourceRaid.Payload is RaidEventPayload raidPayload
                    ? raidPayload.DefenderSnapshotId
                    : appliedSnapshotId,
                nowUtc);
        }
        state.Ledger.MarkApplied(sourceRaid.EventId, appliedSnapshotId, nowUtc);
        state.Ledger.ReportApplicationResult(
            sourceRaid.EventId,
            EventApplicationResultKind.Applied,
            failureReason: null,
            nextRetryAtUtc: null);
        state.EventNotifications.SignalUser(settlementEvent.Target.UserId);
        return new RaidSettlementOperationResult(
            RaidSettlementOperationResultKind.Completed,
            appliedSnapshotId,
            settlementEvent.EventId,
            null);
    }

    private static bool TryValidateSource(
        AuthoritativeEvent sourceRaid,
        RaidSettlementDeferredPayload payload,
        out RaidEventPayload? raidPayload,
        out string? failure)
    {
        raidPayload = sourceRaid.Payload as RaidEventPayload;
        failure = null;
        if (sourceRaid.Type != ServerEventType.Raid
            || raidPayload is not { OpponentKind: RaidOpponentKind.Player }
            || !raidPayload.RequiresSettlement)
        {
            failure = "Source event is not a player raid requiring settlement.";
            return false;
        }

        if (!string.Equals(sourceRaid.Actor.UserId, payload.AttackerUserId, StringComparison.Ordinal)
            || !string.Equals(sourceRaid.Actor.ColonyId, payload.AttackerColonyId, StringComparison.Ordinal)
            || !string.Equals(sourceRaid.Target.UserId, payload.DefenderUserId, StringComparison.Ordinal)
            || !string.Equals(sourceRaid.Target.ColonyId, payload.DefenderColonyId, StringComparison.Ordinal)
            || !string.Equals(raidPayload.DefenderSnapshotId, payload.DefenderSnapshotId, StringComparison.Ordinal))
        {
            failure = "Raid settlement parties or defender snapshot do not match the source event.";
            return false;
        }

        return true;
    }

    private static bool IdentityMatchesEvidence(
        SnapshotIdentity identity,
        RaidSettlementDeferredPayload payload)
    {
        return string.Equals(identity.OwnerId, payload.AttackerUserId, StringComparison.Ordinal)
            && string.Equals(identity.ColonyId, payload.AttackerColonyId, StringComparison.Ordinal)
            && string.Equals(identity.SnapshotId, payload.EvidenceSnapshotId, StringComparison.Ordinal);
    }

    private static AuthoritativeEvent? FindSettlementEvent(IAuthoritativeEventLedger ledger, string raidEventId)
    {
        string key = "raid-settlement:" + raidEventId;
        return ledger.FindByIdempotencyKey(key)
            ?? ledger.ListAll().FirstOrDefault(evt =>
                evt.Type == ServerEventType.Raid
                && evt.Payload is RaidEventPayload { Settlement: not null }
                && evt.IdempotencyKey.StartsWith(key + ":", StringComparison.Ordinal));
    }

    private static RaidSettlementOperationResult ManualReview(
        ClashOfRimNetworkState state,
        string raidEventId,
        string message)
    {
        if (state.Ledger.Find(raidEventId) is not null)
        {
            state.Ledger.ReportApplicationResult(
                raidEventId,
                EventApplicationResultKind.NeedsManualReview,
                message,
                nextRetryAtUtc: null);
        }

        state.RuntimeLogger.LogError(
            "Deferred raid settlement requires manual review: raid={RaidEventId} reason={Reason}",
            raidEventId,
            message);
        return new RaidSettlementOperationResult(
            RaidSettlementOperationResultKind.ManualReview,
            null,
            null,
            message);
    }

    private static string BuildSettlementSnapshotId(string colonyId, string raidEventId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raidEventId));
        return SanitizeSnapshotIdPart(colonyId)
            + "-raid-settled-"
            + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string SanitizeSnapshotIdPart(string value)
    {
        char[] chars = (value ?? string.Empty).Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                chars[i] = '-';
            }
        }

        string sanitized = new(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "snapshot" : sanitized;
    }

    private static string ResolveEvidenceMapId(
        SaveSnapshotIndex evidenceIndex,
        string targetMapId,
        string raidEventId)
    {
        HashSet<string> raidBattleWorldObjectIds = evidenceIndex.WorldObjects
            .Where(worldObject => string.Equals(worldObject.Def, "ClashOfRim_RemoteRaidBattleMapParent", StringComparison.Ordinal))
            .Where(worldObject => string.Equals(worldObject.ClashOfRimRelatedEventId, raidEventId, StringComparison.Ordinal))
            .Select(worldObject => worldObject.UniqueLoadId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
        string? raidBattleMapId = evidenceIndex.Maps
            .Where(map => !string.IsNullOrWhiteSpace(map.UniqueId))
            .FirstOrDefault(map => !string.IsNullOrWhiteSpace(map.ParentWorldObjectId)
                && raidBattleWorldObjectIds.Contains(map.ParentWorldObjectId!))
            ?.UniqueId;
        if (!string.IsNullOrWhiteSpace(raidBattleMapId))
        {
            return raidBattleMapId!;
        }

        string normalizedTargetMapId = NormalizeMapId(targetMapId);
        return normalizedTargetMapId;
    }

    private static string NormalizeMapId(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return string.Empty;
        }

        string trimmed = mapId.Trim();
        return trimmed.StartsWith("Map_", StringComparison.Ordinal)
            ? trimmed["Map_".Length..]
            : trimmed;
    }
}
