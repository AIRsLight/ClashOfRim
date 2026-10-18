using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

// Executes one registered intent while its coordinator owns the mutation/business gates and applying scope.
// Checkpoints are acknowledged only after their durable replacement and pending-state publication succeed.
internal sealed class SnapshotCommitApplication(
    IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
    Action<LatestSnapshotRecord, string?> postUpload, IAuthoritativeEventLedger? ledger,
    Action<string>? signalUser, Action<AuthoritativeEvent>? recordTrade, ServerShopRegistry? shop,
    MercenaryContractRegistry? contracts, MercenaryGuardContractRegistry? guards, PlayerRegistry? players,
    Func<SnapshotConfirmationMutation, LatestSnapshotRecord, SnapshotConfirmationPlan>? planConfirmation,
    SnapshotConfirmationReceipts receipts, ISnapshotCommitCheckpoint checkpoint)
{
    internal void Apply(SnapshotCommitIntent intent)
    {
        SaveSnapshotPackage package = intent.Package;
        SnapshotIdentity identity = package.Envelope.Identity;
        var snapshot = new LatestSnapshotRecord(identity, package.Envelope, package.Index, intent.AcceptedAtUtc);
        if (intent.Upload?.Confirmation is { } confirmation)
        {
            if (ledger is null || recordTrade is null || planConfirmation is null)
                throw new InvalidOperationException("Confirmation recovery requires its ledger, planner and achievement recorder.");
            confirmation.Preflight(ledger);
        }
        if (intent.PendingOperations is { } pendingOperations)
        {
            bank.ValidatePendingOperations(pendingOperations, snapshot);
            contracts?.ValidatePendingOperations(pendingOperations, snapshot);
        }
        if (intent.Shop is { } pendingShop) shop!.ValidateSnapshotCommit(pendingShop);
        intent.Mercenary?.Preflight(contracts, guards);
        intent.Raid?.Preflight(ledger!, guards!);
        LedgerBatch? settlementBatch = null;
        if (intent.RaidCleanup is { } pendingCleanup)
        {
            if (ledger is null || players is null) throw new InvalidOperationException("Raid cleanup recovery requires the ledger and players.");
            pendingCleanup.Preflight(ledger);
        }
        if (intent.RaidSettlement is { } pendingSettlement)
        {
            if (ledger is null || players is null) throw new InvalidOperationException("Raid settlement recovery requires the ledger and players.");
            settlementBatch = pendingSettlement.Preflight(ledger);
        }
        if (intent.Trade is { } pendingTrade)
        {
            if (ledger is null) throw new InvalidOperationException("Trade recovery requires the event ledger.");
            LedgerBatchValidation.Validate(pendingTrade.Batch, ledger.Find, ledger.FindByIdempotencyKey);
        }
        if (intent.Gift is { } pendingGift)
        {
            if (ledger is null) throw new InvalidOperationException("Gift recovery requires the event ledger.");
            try { LedgerBatchValidation.Validate(new LedgerBatch([pendingGift], []), ledger.Find, ledger.FindByIdempotencyKey); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("Gift journal conflicts with an existing event.", ex); }
        }
        if (intent.Support is { } pendingSupport)
        {
            if (ledger is null) throw new InvalidOperationException("Support recovery requires the event ledger.");
            pendingSupport.Preflight(ledger);
        }
        LatestSnapshotRecord? latest = snapshots.GetLatest(identity.OwnerId!, identity.ColonyId!);
        bool alreadyPublished = latest?.Identity.SnapshotId == identity.SnapshotId
            && latest?.Envelope.NextLineageToken == package.Envelope.NextLineageToken
            && latest?.Envelope.OriginalSha256 == package.Envelope.OriginalSha256;
        bool stillAtParent = latest?.Identity.SnapshotId == intent.PreviousSnapshotId
            && latest?.Envelope.NextLineageToken == intent.PreviousLineageToken;
        SaveSnapshotEnvelope? maintenancePrevious = intent.RaidSettlement?.Previous ?? intent.RaidCleanup?.Previous;
        if (maintenancePrevious is not null)
        {
            alreadyPublished = SameVersion(latest?.Envelope, package.Envelope);
            stillAtParent = SameVersion(latest?.Envelope, maintenancePrevious);
        }
        if (intent.Upload is { } pendingUpload)
        {
            alreadyPublished = SameVersion(latest?.Envelope, package.Envelope);
            stillAtParent = pendingUpload.Previous is null ? latest is null : SameVersion(latest?.Envelope, pendingUpload.Previous);
        }
        if (!stillAtParent && !alreadyPublished)
            throw new InvalidDataException("A pending snapshot commit conflicts with a newer snapshot; refusing to overwrite it.");
        if (!alreadyPublished)
        {
            if (snapshots is IPreparedColonySnapshotPackageStore preparedStore && intent.PreparedStoragePayload is { } preparedPayload)
                preparedStore.StorePrepared(package, package.Index, intent.AcceptedAtUtc, preparedPayload);
            else if (snapshots is IColonySnapshotPackageStore packages) packages.StoreLatest(package, package.Index, intent.AcceptedAtUtc);
            else snapshots.StoreLatest(snapshot);
        }
        if (intent.RaidCleanup is { } raidCleanup)
        {
            players!.RecordLatestSnapshotReference(identity.OwnerId!, identity.ColonyId!, identity.SnapshotId, intent.AcceptedAtUtc);
            ledger!.ApplyBatch(raidCleanup.Batch);
            foreach (string user in raidCleanup.Batch.Events.Select(item => item.Target.UserId).Distinct(StringComparer.Ordinal))
                signalUser?.Invoke(user);
        }
        else if (intent.RaidSettlement is { } raidSettlement)
        {
            players!.RecordLatestSnapshotReference(identity.OwnerId!, identity.ColonyId!, identity.SnapshotId, intent.AcceptedAtUtc);
            ledger!.ApplyBatch(settlementBatch!);
            signalUser?.Invoke(raidSettlement.Settlement.Target.UserId);
        }
        else if (intent.Raid is { } raid)
        {
            raid.Apply(ledger!, guards!);
            signalUser?.Invoke(raid.Raid.Target.UserId);
        }
        else if (intent.Support is { } support)
        {
            support.Apply(ledger!);
            signalUser?.Invoke(support.Support.Target.UserId);
        }
        else if (intent.Gift is { } gift)
        {
            LedgerAppendResult append = ledger!.Append(gift);
            if (append.Event.EventId != gift.EventId)
                throw new InvalidDataException("Gift idempotency key was claimed by another event.");
            // Repeat the wake-up on recovery even if Append already succeeded before interruption.
            signalUser?.Invoke(gift.Target.UserId);
        }
        else if (intent.Trade is { } trade)
        {
            ledger!.ApplyBatch(trade.Batch);
            recordTrade?.Invoke(trade.Batch.Events[0]);
            foreach (string userId in trade.NotifyUsers) signalUser?.Invoke(userId);
        }
        else if (intent.Shop is { } shopMutation) shop!.ApplySnapshotCommit(shopMutation);
        else if (intent.Mercenary is { } mercenary) mercenary.Apply(contracts, guards);
        else if (intent.Loan is not null || intent.Debt is not null) bank.ApplySnapshotCommit(intent.Loan, intent.Debt);
        if (intent.PendingOperations is { } confirmations)
        {
            bank.ApplyPendingOperations(confirmations, snapshot);
            contracts?.ApplyPendingOperations(confirmations, snapshot);
        }
        // Processors are idempotent; retain the intent if scheduling downstream work fails.
        // A pure confirmation intent only owns these records. Its caller still owns event/raid-specific post-upload data.
        if (intent.Upload?.Confirmation?.Plan is null && (intent.Loan is not null || intent.Debt is not null || intent.Gift is not null || intent.Trade is not null
            || intent.Shop is not null || intent.Mercenary is not null || intent.Raid is not null || intent.Upload is not null || intent.Support is not null)) postUpload(snapshot, intent.SnapshotUploadKind);
        if (intent.Upload?.Confirmation is { } application)
        {
            if (application.Plan is null)
            {
                application = application with { Plan = planConfirmation!(application, snapshot) };
                application.Validate(snapshot);
                intent = intent with { Upload = intent.Upload with { Confirmation = application } };
                checkpoint.Persist(intent);
            }
            application.Preflight(ledger!);
            ledger!.ApplyBatch(application.Plan!.Batch);
            foreach (var item in application.Plan.AchievementEvents) recordTrade!(item);
            receipts.Store(new SnapshotConfirmationReceipt(1, snapshot.Envelope, snapshot.AcceptedAtUtc,
                application.Request, application.Plan.Results));
        }
    }

    internal static bool SameVersion(SaveSnapshotEnvelope? left, SaveSnapshotEnvelope right) => left is not null
        && left.Identity == right.Identity && left.OriginalSha256 == right.OriginalSha256
        && left.PayloadSha256 == right.PayloadSha256 && left.NextLineageToken == right.NextLineageToken;
}
