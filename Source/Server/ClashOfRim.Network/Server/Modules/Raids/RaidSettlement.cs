using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidSettlement(IAuthoritativeEventLedger ledger, IColonySnapshotIndexStore snapshots,
    SnapshotMutationCoordinator mutations, SnapshotCommitService commits, BankLoanRegistry loans, PlayerRegistry players,
    EventNotificationHub notifications, OnlinePresenceRegistry presence, AdminBaselineRegistry admin,
    ServerPluginRegistry plugins, CompatibilityBaselineRegistry compatibility,
    Func<ClashOfRimServerConfiguration> configuration, Func<ILogger> logger)
{
    internal RaidSettlementOperationResult Execute(
        RaidSettlementDeferredPayload payload,
        Func<SaveSnapshotPackage> readEvidence,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(readEvidence);

        using (mutations.Enter()) { }
        AuthoritativeEvent? sourceRaid = ledger.Find(payload.RaidEventId);
        if (sourceRaid is null)
        {
            return ManualReview(payload.RaidEventId, "Raid settlement source event was not found.");
        }

        if (!TryValidateSource(sourceRaid, payload, out _, out string? sourceFailure))
            return ManualReview(payload.RaidEventId, sourceFailure!);
        AuthoritativeEvent preparedSource = sourceRaid;
        AuthoritativeEvent? existingSettlement = FindSettlementEvent(ledger, payload.RaidEventId);
        if (sourceRaid.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return new RaidSettlementOperationResult(
                RaidSettlementOperationResultKind.AlreadyCompleted,
                sourceRaid.AppliedSnapshotId,
                existingSettlement?.EventId,
                null);
        }

        if (snapshots is not IColonySnapshotPackageStore packageStore)
        {
            return ManualReview(payload.RaidEventId, "Raid settlement requires a snapshot package store.");
        }

        var evidencePackage = readEvidence();
        if (!IdentityMatchesEvidence(evidencePackage.Envelope.Identity, payload))
        {
            return ManualReview(payload.RaidEventId, "Raid settlement evidence identity does not match the scheduled operation.");
        }

        SaveSnapshotPackage? originalDefensePackage = packageStore.GetLatestPackage(
            payload.DefenderUserId,
            payload.DefenderColonyId);
        if (originalDefensePackage is null)
        {
            return ManualReview(payload.RaidEventId, "Raid defender snapshot package was not found.");
        }

        if (!string.Equals(
                originalDefensePackage.Envelope.Identity.SnapshotId,
                payload.DefenderSnapshotId,
                StringComparison.Ordinal))
        {
            return ManualReview(payload.RaidEventId, "Raid defender snapshot changed before settlement completed.");
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
                LossRatio: configuration().RaidSettlementLossRatio,
                PackableBuildingDefNames: admin.Current?.PackableBuildingDefNames,
                BuildingMaxHitPointsByDefName: admin.Current?.EstimatedSettlementMaxHitPointsByDefName,
                StuffHitPointFactorByDefName: admin.Current?.StuffHitPointFactorByDefName,
                StuffHitPointOffsetByDefName: admin.Current?.StuffHitPointOffsetByDefName,
                MinimumRemainingHitPointsRatio: configuration().RaidSettlementMinimumRemainingHitPointsRatio,
                IgnoredThingDefNames: plugins.ActiveIgnoredRaidSettlementThingDefNames(compatibility.Current),
                ReturnedMapUniqueId: evidenceMapId,
                BuildingHitPointsLossRatio: configuration().RaidSettlementBuildingHitPointsLossRatio,
                TrapDefNames: admin.Current?.ApprovedTrapDefNames));
        if (!settlement.Accepted)
        {
            return ManualReview(payload.RaidEventId, $"Raid settlement was rejected: {settlement.Kind}.");
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
                plugins.ActiveRaidSettlementSnapshotEditorExtensions(compatibility.Current));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException)
        {
            return ManualReview(payload.RaidEventId, "Raid defender snapshot could not be edited: " + ex.Message);
        }

        if (!string.Equals(editedPackage.Envelope.Identity.OwnerId, payload.DefenderUserId, StringComparison.Ordinal)
            || !string.Equals(editedPackage.Envelope.Identity.ColonyId, payload.DefenderColonyId, StringComparison.Ordinal))
        {
            return ManualReview(payload.RaidEventId, "Edited raid defender snapshot identity does not match the scheduled operation.");
        }

        var preparedPayload = SnapshotPreparationService.Run(() =>
            (packageStore as IPreparedColonySnapshotPackageStore)?.PreparePayload(editedPackage.Payload));
        AuthoritativeEvent settlementEvent;
        using (mutations.Enter())
        {
            sourceRaid = ledger.Find(payload.RaidEventId);
            existingSettlement = FindSettlementEvent(ledger, payload.RaidEventId);
            if (sourceRaid?.Status == ServerEventStatus.AppliedToSnapshot)
                return new RaidSettlementOperationResult(RaidSettlementOperationResultKind.AlreadyCompleted,
                    sourceRaid.AppliedSnapshotId, existingSettlement?.EventId, null);
            if (sourceRaid is null || !TryValidateSource(sourceRaid, payload, out _, out _)
                || !RaidSettlementSnapshotMutation.SameSource(sourceRaid, preparedSource))
                return ManualReviewUnderMutation(payload.RaidEventId, "Raid settlement source changed before commit.");
            if (existingSettlement is not null)
                return ManualReviewUnderMutation(payload.RaidEventId, "An unjournaled settlement event already exists; automatic replay is unsafe.");
            if (!SnapshotCommitService.SameVersion(packageStore.GetLatest(payload.DefenderUserId, payload.DefenderColonyId)?.Envelope,
                    originalDefensePackage.Envelope))
                return ManualReviewUnderMutation(payload.RaidEventId, "Raid defender snapshot changed before settlement commit.");

            settlementEvent = RaidSettlementLedgerRecorder.CreateSettlementEvent(sourceRaid, settlement, nowUtc,
                presence.IsUserOnline(sourceRaid.Target.UserId));
            settlementEvent = settlementEvent with { Payload = ((RaidEventPayload)settlementEvent.Payload) with
                { EvidenceRequestHash = payload.Origin == RaidSettlementOrigin.OnlineEvidence ? payload.RequestHash : null } };
            var mutation = new RaidSettlementSnapshotMutation(sourceRaid, settlementEvent, originalDefensePackage.Envelope,
                editedSnapshotId, nowUtc);
            commits.CommitRaidSettlement(packageStore, loans, ledger, players,
                editedPackage, mutation, user => notifications.SignalUser(user), preparedPayload);
        }
        logger().LogInformation(
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

    private RaidSettlementOperationResult ManualReview(
        string raidEventId,
        string message)
    {
        using var mutation = mutations.Enter();
        return ManualReviewUnderMutation(raidEventId, message);
    }

    private RaidSettlementOperationResult ManualReviewUnderMutation(string raidEventId, string message)
    {
        if (ledger.Find(raidEventId) is { Status: ServerEventStatus.AppliedToSnapshot } completed)
            return new RaidSettlementOperationResult(RaidSettlementOperationResultKind.AlreadyCompleted,
                completed.AppliedSnapshotId, FindSettlementEvent(ledger, raidEventId)?.EventId, null);
        if (ledger.Find(raidEventId) is not null)
        {
            ledger.ReportApplicationResult(
                raidEventId,
                EventApplicationResultKind.NeedsManualReview,
                message,
                nextRetryAtUtc: null);
        }

        logger().LogError(
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
