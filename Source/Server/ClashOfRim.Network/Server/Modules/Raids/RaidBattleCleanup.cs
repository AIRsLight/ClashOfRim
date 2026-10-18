using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.Modules.Raids.RaidTimeoutRules;
using static AIRsLight.ClashOfRim.Network.Modules.Support.SupportClosures;
using static AIRsLight.ClashOfRim.Network.Modules.Support.SupportReturns;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidBattleCleanup(IAuthoritativeEventLedger ledger, IColonySnapshotIndexStore snapshots,
    SnapshotPostUploadJobRegistry jobs, SnapshotMutationCoordinator mutations, RaidQueries queries,
    SnapshotCommitService commits, BankLoanRegistry loans, PlayerRegistry players, OnlinePresenceRegistry presence,
    EventNotificationHub notifications, Func<ILogger> logger)
{
    internal RaidCleanupEventAppendResult CleanupTimeouts(
        IReadOnlyList<AuthoritativeEvent> failedRaids,
        DateTimeOffset nowUtc)
    {
        if (failedRaids.Count == 0 || snapshots is not IColonySnapshotPackageStore packageStore)
            return RaidCleanupEventAppendResult.Empty;
        var attackerLossEvents = new List<AuthoritativeEvent>();
        var supportLossNotifications = new List<AuthoritativeEvent>();
        var completedRaids = new List<RaidCleanupCompletion>();
        foreach (var raid in failedRaids)
        {
            if (raid.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player }
                || string.IsNullOrWhiteSpace(raid.Actor.UserId) || string.IsNullOrWhiteSpace(raid.Actor.ColonyId)) continue;
            string owner = raid.Actor.UserId, colony = raid.Actor.ColonyId;
            var package = SnapshotPreparationService.Run(() => packageStore.GetLatestPackage(owner, colony));
            if (package is null)
            {
                logger().LogWarning("Timed out raid cleanup skipped missing snapshot: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId}",
                    raid.EventId, owner, colony);
                continue;
            }
            using (mutations.Enter())
            {
                var current = ledger.Find(raid.EventId);
                if (!SameSource(current, raid) || !CanFinish(jobs, current!)
                    || !SnapshotCommitService.SameVersion(packageStore.GetLatest(owner, colony)?.Envelope, package.Envelope)) continue;
                if (!package.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent"))
                {
                    // Publication may already have completed before its acknowledgement failed.
                    completedRaids.Add(new RaidCleanupCompletion(current!, package.Envelope));
                    continue;
                }
                if (!CanCleanup(current!, package)) continue;
            }

            PreparedCleanup prepared;
            try
            {
                prepared = Prepare(packageStore, package, raid.EventId, nowUtc);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or InvalidOperationException)
            {
                logger().LogWarning(ex, "Timed out raid cleanup preparation failed: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId}",
                    raid.EventId, owner, colony);
                continue;
            }
            if (!prepared.Changed)
            {
                logger().LogWarning("Timed out raid cleanup found no removable battle: raid={RaidEventId} sourceSnapshot={SourceSnapshotId}",
                    raid.EventId, package.Envelope.Identity.SnapshotId);
                continue;
            }
            using (mutations.Enter())
            {
                lock (jobs.SnapshotCommitGate)
                {
                    var current = ledger.Find(raid.EventId);
                    if (!SameSource(current, raid) || !CanFinish(jobs, current!) || current!.Payload is not RaidEventPayload
                        || !SnapshotCommitService.SameVersion(packageStore.GetLatest(owner, colony)?.Envelope, package.Envelope)
                        || !CanCleanup(current, package)) continue;
                    var mutation = Commit(packageStore, current, package, prepared, nowUtc, "timeout");
                    attackerLossEvents.AddRange(mutation.Batch.Events.Where(item => item.Type == ServerEventType.Raid));
                    supportLossNotifications.AddRange(mutation.Batch.Events.Where(item => item.Type == ServerEventType.SupportPawn));
                    completedRaids.Add(new RaidCleanupCompletion(current, prepared.Package.Envelope));
                }
            }
            logger().LogInformation(
                "Timed out raid cleanup stored snapshot: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId} sourceSnapshot={SourceSnapshotId} cleanedSnapshot={CleanedSnapshotId}",
                raid.EventId, owner, colony, package.Envelope.Identity.SnapshotId, prepared.Package.Envelope.Identity.SnapshotId);
        }
        return new RaidCleanupEventAppendResult(attackerLossEvents, supportLossNotifications, completedRaids);
    }

    internal SaveSnapshotPackage BeforeDownload(
        SaveSnapshotPackage package,
        string userId,
        string colonyId,
        DateTimeOffset nowUtc,
        Func<ProtocolResponse?>? authorize = null)
    {
        if (snapshots is not IColonySnapshotPackageStore packageStore) return package;
        if (!package.Index.WorldObjects.Any(worldObject =>
                worldObject.Def == "ClashOfRim_RemoteRaidBattleMapParent")) return package;
        AuthoritativeEvent raid;
        using (var recovery = mutations.Enter(authorize))
        {
            if (recovery.Rejection is not null) return package;
            if (!SnapshotCommitService.SameVersion(packageStore.GetLatest(userId, colonyId)?.Envelope, package.Envelope)) return package;
            string? raidEventId = package.Index.WorldObjects
                .Where(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent")
                .Select(item => item.ClashOfRimRelatedEventId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
            var source = string.IsNullOrWhiteSpace(raidEventId) ? null : ledger.Find(raidEventId);
            if (source?.Payload is not RaidEventPayload || !CanCleanup(source, package)) return package;
            raid = source;
        }

        PreparedCleanup prepared;
        try
        {
            prepared = Prepare(packageStore, package, raid.EventId, nowUtc);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or InvalidOperationException)
        {
            logger().LogWarning(ex,
                "Failed to prepare stale raid battle cleanup: user={UserId} colony={ColonyId} sourceSnapshot={SourceSnapshotId}",
                userId, colonyId, package.Envelope.Identity.SnapshotId);
            return package;
        }
        if (!prepared.Changed) return package;

        using (var recovery = mutations.Enter(authorize))
        {
            if (recovery.Rejection is not null) return package;
            // Preparation may overlap an upload, deletion, a new raid or a support closure.
            // Rebuild the loss batch from current support records only after revalidating the source.
            var current = ledger.Find(raid.EventId);
            if (!SnapshotCommitService.SameVersion(packageStore.GetLatest(userId, colonyId)?.Envelope, package.Envelope)
                || current?.Payload is not RaidEventPayload || !SameSource(current, raid)
                || !CanCleanup(current, package)) return package;
            Commit(packageStore, current, package, prepared, nowUtc, "map-cleanup");
        }
        logger().LogInformation(
            "Cleaned stale raid battle snapshot before download: user={UserId} colony={ColonyId} sourceSnapshot={SourceSnapshotId} cleanedSnapshot={CleanedSnapshotId} removedMaps={RemovedMapCount} lostAttackPawns={LostAttackPawns} lostSupportPawns={LostSupportPawns}",
            userId, colonyId, package.Envelope.Identity.SnapshotId, prepared.Package.Envelope.Identity.SnapshotId,
            prepared.Result.RemovedMapCount, prepared.Result.LostAttackPawns.Count, prepared.Result.LostSupportPawns.Count);
        return prepared.Package;
    }

    internal RaidCleanupSnapshotMutation BuildMutation(
        AuthoritativeEvent raid,
        RaidEventPayload raidPayload,
        SaveSnapshotPackage originalAttackerPackage,
        SaveSnapshotPackage cleanedAttackerPackage,
        RaidAttackerSnapshotCleanupResult cleanupResult,
        DateTimeOffset nowUtc,
        string lossReason = "timeout")
    {
        var events = new List<AuthoritativeEvent>();
        var supportSources = new List<AuthoritativeEvent>();
        var statuses = new List<LedgerStatusChange>();
        if (cleanupResult.LostAttackPawns.Count > 0)
        {
            string cleanedSnapshotId = cleanedAttackerPackage.Envelope.Identity.SnapshotId ?? string.Empty;
            RaidAttackerLossRecord loss = new(
                raid.EventId,
                cleanedSnapshotId,
                cleanupResult.LostAttackPawns.Select(pawn => pawn.GlobalKey).ToList(),
                raidPayload.AttackForce?.CarriedThings ?? Array.Empty<EventThingReference>(),
                lossReason);

            AuthoritativeEvent lossEvent = AuthoritativeEventFactory.CreateForLedger(ledger,
                ServerEventType.Raid,
                new EventParty("server"),
                raid.Actor,
                $"raid-attacker-loss:{raid.EventId}:{cleanedSnapshotId}",
                presence.IsUserOnline(raid.Actor.UserId),
                new RaidEventPayload(
                    raidPayload.DefenderSnapshotId,
                    ReturnedSnapshotId: null,
                    raidPayload.StartedAtUtc,
                    FinishedAtUtc: nowUtc,
                    Settlement: null,
                    AttackForce: raidPayload.AttackForce,
                    AttackerLoss: loss),
                nowUtc,
                raid.TargetContext);

            events.Add(lossEvent);
        }

        IReadOnlyList<AuthoritativeEvent> allEvents = ledger.ListAll();
        foreach (RaidBattleLostSupportPawnSummary lostSupport in cleanupResult.LostSupportPawns.DistinctBy(pawn => pawn.EventId))
        {
            AuthoritativeEvent? supportEvent = ledger.Find(lostSupport.EventId);
            if (supportEvent?.Payload is not SupportPawnEventPayload payload)
            {
                continue;
            }

            if (supportEvent.Status is ServerEventStatus.Cancelled
                or ServerEventStatus.Failed
                or ServerEventStatus.RejectedByTarget
                or ServerEventStatus.Conflict
                || HasSupportFinishEvent(supportEvent, allEvents))
            {
                continue;
            }

            AuthoritativeEvent notification = CreateSupportPawnLossReturnEvent(
                supportEvent,
                payload,
                lostSupport.PawnName ?? payload.PawnName,
                "RaidSettlementLost",
                presence.IsUserOnline(supportEvent.Actor.UserId),
                nowUtc);
            events.Add(notification);
            supportSources.Add(supportEvent);
            statuses.Add(new LedgerStatusChange(supportEvent.EventId, supportEvent.Status, ServerEventStatus.Failed));
        }
        return new RaidCleanupSnapshotMutation(raid, originalAttackerPackage.Envelope, new LedgerBatch(events, statuses), supportSources);
    }

    private bool CanCleanup(AuthoritativeEvent raid, SaveSnapshotPackage package)
    {
        if (raid.Actor.UserId != package.Envelope.Identity.OwnerId || raid.Actor.ColonyId != package.Envelope.Identity.ColonyId
            || queries.ActiveAttacker(raid.Actor.UserId, raid.Actor.ColonyId) is not null) return false;
        var battles = package.Index.WorldObjects.Where(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent").ToArray();
        return battles.Length > 0 && battles.All(item => item.ClashOfRimRelatedEventId == raid.EventId);
    }

    private static PreparedCleanup Prepare(IColonySnapshotPackageStore store, SaveSnapshotPackage original,
        string raidEventId, DateTimeOffset at) => SnapshotPreparationService.Run(() =>
    {
        bool changed = RaidAttackerSnapshotCleanupEditor.TryRemoveRaidBattleState(original,
            RaidSnapshotIds.Cleanup(original.Envelope.Identity.ColonyId!, raidEventId, at), at,
            out var cleaned, out RaidAttackerSnapshotCleanupResult result);
        var storage = changed ? (store as IPreparedColonySnapshotPackageStore)?.PreparePayload(cleaned.Payload) : null;
        return new PreparedCleanup(changed, cleaned, result, storage);
    });

    private RaidCleanupSnapshotMutation Commit(IColonySnapshotPackageStore store, AuthoritativeEvent source,
        SaveSnapshotPackage original, PreparedCleanup prepared, DateTimeOffset at, string reason)
    {
        var mutation = BuildMutation(source, (RaidEventPayload)source.Payload, original, prepared.Package, prepared.Result, at, reason);
        commits.CommitRaidCleanup(store, loans, ledger, players, prepared.Package, mutation, at,
            user => notifications.SignalUser(user), prepared.Storage);
        return mutation;
    }

    private sealed record PreparedCleanup(bool Changed, SaveSnapshotPackage Package,
        RaidAttackerSnapshotCleanupResult Result, PreparedSnapshotPayload? Storage);
}

internal sealed record RaidCleanupCompletion(AuthoritativeEvent Raid, SaveSnapshotEnvelope Snapshot);

internal sealed record RaidCleanupEventAppendResult(
    IReadOnlyList<AuthoritativeEvent> AttackerLossEvents,
    IReadOnlyList<AuthoritativeEvent> SupportLossNotifications,
    IReadOnlyList<RaidCleanupCompletion> CompletedRaids)
{
    public static RaidCleanupEventAppendResult Empty { get; } = new(
        Array.Empty<AuthoritativeEvent>(),
        Array.Empty<AuthoritativeEvent>(),
        Array.Empty<RaidCleanupCompletion>());
}
