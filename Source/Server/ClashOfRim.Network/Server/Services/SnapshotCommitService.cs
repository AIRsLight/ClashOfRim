using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

/// <summary>A write-ahead intent bridges snapshot files and business records. Call under the snapshot mutation gate.</summary>
public sealed partial class SnapshotCommitService
{
    private readonly string? directory;
    private readonly SnapshotCommitJournal journal;
    private readonly Dictionary<string, SnapshotCommitIntent> pending = new(StringComparer.Ordinal);
    private bool applying;
    internal bool HasPending => pending.Count != 0;
    private int pendingUploads;
    internal bool HasPendingUploads => Volatile.Read(ref pendingUploads) != 0;
    internal bool IsApplying => applying;

    public SnapshotCommitService(string? directory = null)
    {
        this.directory = directory;
        journal = new SnapshotCommitJournal(directory);
        if (directory is null) return;
        foreach (var receipt in ReadReceipts(directory)) receipts.TryAdd(ReceiptKey(receipt.Envelope), receipt);
        foreach (SnapshotCommitIntent intent in journal.ReadPending())
        {
            pending.Add(intent.Id, intent);
            if (intent.Upload is not null) pendingUploads++;
        }
    }

    public static void ValidateDirectory(string directory)
    {
        SnapshotCommitJournal.ValidateDirectory(directory);
        foreach (SnapshotConfirmationReceipt _ in ReadReceipts(directory)) { }
    }

    public void Commit(
        IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, SaveSnapshotPackage package,
        LatestSnapshotRecord snapshot, BankLoanRecord? loan, BankDebtRecord? debt,
        Action<LatestSnapshotRecord, string?> postUpload, string? snapshotUploadKind = null, MercenaryContractRegistry? contracts = null, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        if (loan is null && debt is null) throw new ArgumentException("A bank mutation is required.");
        EnsureNoPendingCommit();
        SnapshotIdentity identity = snapshot.Identity;
        LatestSnapshotRecord? previous = snapshots.GetLatest(identity.OwnerId!, identity.ColonyId!);
        var intent = new SnapshotCommitIntent(1, Guid.NewGuid().ToString("N"),
            previous?.Identity.SnapshotId, previous?.Envelope.NextLineageToken,
            package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, snapshot.AcceptedAtUtc, loan, debt, snapshotUploadKind);
        intent = AttachPendingOperations(intent, bank, contracts);
        Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, postUpload, contracts: contracts);
    }

    public void Recover(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, Action<LatestSnapshotRecord, string?> postUpload)
        => Recover(snapshots, bank, postUpload, null);

    public void Recover(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        Action<LatestSnapshotRecord, string?> postUpload, IAuthoritativeEventLedger? ledger,
        Action<string>? signalUser = null, Action<AuthoritativeEvent>? recordTrade = null, ServerShopRegistry? shop = null,
        MercenaryContractRegistry? contracts = null, MercenaryGuardContractRegistry? guards = null, PlayerRegistry? players = null,
        Action<LatestSnapshotRecord, string?, SnapshotUploadMutation>? resumeUpload = null,
        Func<SnapshotConfirmationMutation, LatestSnapshotRecord, SnapshotConfirmationPlan>? planConfirmation = null)
    {
        if (applying) return; // A post-upload processor may re-enter the snapshot mutation coordinator.
        foreach (SnapshotCommitIntent original in pending.Values.ToArray())
        {
            var intent = original;
            if (intent.Version < 6)
            {
                // Upgrade old intents before replay, excluding operations created after their accepted time.
                intent = AttachPendingOperations(intent, bank, contracts, legacyRecovery: true);
                if (intent != original)
                {
                    journal.Write(intent, replace: true);
                    pending[intent.Id] = intent;
                }
            }
            Action<LatestSnapshotRecord, string?> callback = postUpload;
            if (intent.Upload is { } upload)
            {
                if (resumeUpload is null) throw new InvalidOperationException("Ordinary upload recovery requires its original post-processing context.");
                callback = (snapshot, kind) => resumeUpload(snapshot, kind, upload);
            }
            Apply(intent, snapshots, bank, callback, ledger, signalUser, recordTrade, shop, contracts, guards, players, planConfirmation);
        }
    }

    internal void CommitUpload(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, MercenaryContractRegistry contracts,
        SaveSnapshotPackage package, LatestSnapshotRecord snapshot, PendingSnapshotOperations operations,
        string? kind, string? confirmationOperation, IReadOnlyCollection<SnapshotAchievementCandidateDto>? candidates,
        Action<LatestSnapshotRecord, string?, SnapshotUploadMutation> postUpload, SnapshotConfirmationRequest? confirmation = null,
        IAuthoritativeEventLedger? ledger = null, Action<AuthoritativeEvent>? recordTrade = null,
        Func<SnapshotConfirmationMutation, LatestSnapshotRecord, SnapshotConfirmationPlan>? planConfirmation = null, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        EnsureNoPendingCommit();
        lock (bank.SnapshotCommitGate)
        lock (contracts.SnapshotCommitGate)
        {
            operations.Validate(snapshot, allowEmpty: true);
            bank.ValidatePendingOperations(operations, snapshot);
            contracts.ValidatePendingOperations(operations, snapshot);
            var previous = snapshots.GetLatest(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!);
            var mutation = new SnapshotUploadMutation(previous?.Envelope, confirmationOperation, candidates?.ToArray() ?? [],
                confirmation is null ? null : SnapshotConfirmationMutation.Capture(confirmation, ledger!));
            var intent = new SnapshotCommitIntent(confirmation is null ? 10 : 11, Guid.NewGuid().ToString("N"), previous?.Identity.SnapshotId,
                previous?.Envelope.NextLineageToken, package with { Envelope = snapshot.Envelope, Index = snapshot.Index },
                snapshot.AcceptedAtUtc, null, null, kind, PendingOperations: operations, Upload: mutation);
            mutation.Validate(intent);
            Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, (accepted, uploadKind) => postUpload(accepted, uploadKind, mutation), ledger: ledger,
                recordTrade: recordTrade, contracts: contracts, planConfirmation: planConfirmation);
        }
    }

    public void CommitPendingOperations(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        MercenaryContractRegistry contracts, SaveSnapshotPackage package, LatestSnapshotRecord snapshot,
        PendingSnapshotOperations operations, string? snapshotUploadKind = null, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        EnsureNoPendingCommit();
        operations.Validate(snapshot);
        bank.ValidatePendingOperations(operations, snapshot);
        contracts.ValidatePendingOperations(operations, snapshot);
        LatestSnapshotRecord? previous = snapshots.GetLatest(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!);
        var intent = new SnapshotCommitIntent(6, Guid.NewGuid().ToString("N"),
            previous?.Identity.SnapshotId, previous?.Envelope.NextLineageToken,
            package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, snapshot.AcceptedAtUtc,
            null, null, snapshotUploadKind, PendingOperations: operations);
        Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, (_, _) => { }, contracts: contracts);
    }

    private static SnapshotCommitIntent AttachPendingOperations(SnapshotCommitIntent intent, BankLoanRegistry bank,
        MercenaryContractRegistry? contracts, bool legacyRecovery = false)
    {
        var snapshot = new LatestSnapshotRecord(intent.Package.Envelope.Identity, intent.Package.Envelope, intent.Package.Index, intent.AcceptedAtUtc);
        var batch = PendingSnapshotOperations.Capture(bank, contracts, snapshot, intent.Loan?.LoanId, intent.Debt?.DebtId);
        if (legacyRecovery)
            batch = batch with
            {
                Loan = batch.Loan is { } loan && (loan.RepaymentRequestedAtUtc ?? loan.CreatedAtUtc) <= intent.AcceptedAtUtc ? loan : null,
                Debts = batch.Debts.Where(debt => (debt.RepaymentRequestedAtUtc ?? debt.CreatedAtUtc) <= intent.AcceptedAtUtc).ToArray(),
                Mercenaries = batch.Mercenaries.Where(contract => contract.CreatedAtUtc <= intent.AcceptedAtUtc).ToArray()
            };
        if (batch.IsEmpty) return intent;
        batch.Validate(snapshot);
        bank.ValidatePendingOperations(batch, snapshot);
        contracts?.ValidatePendingOperations(batch, snapshot);
        return intent with { Version = Math.Max(intent.Version, 6), PendingOperations = batch };
    }

    public void CommitRaid(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, IAuthoritativeEventLedger ledger,
        MercenaryContractRegistry contracts, MercenaryGuardContractRegistry guards, SaveSnapshotPackage package,
        LatestSnapshotRecord snapshot, RaidSnapshotMutation mutation, Action<LatestSnapshotRecord, string?> postUpload,
        Action<string> signalUser, string? snapshotUploadKind = null, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        lock (guards.SnapshotCommitGate)
        {
            EnsureNoPendingCommit();
            mutation.Validate(snapshot.Identity);
            if (mutation.Preflight(ledger, guards)) throw new InvalidOperationException("Use the existing raid instead of committing it again.");
            var previous = snapshots.GetLatest(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!);
            var intent = new SnapshotCommitIntent(7, Guid.NewGuid().ToString("N"), previous?.Identity.SnapshotId,
                previous?.Envelope.NextLineageToken, package with { Envelope = snapshot.Envelope, Index = snapshot.Index },
                snapshot.AcceptedAtUtc, null, null, snapshotUploadKind, Raid: mutation);
            intent = AttachPendingOperations(intent, bank, contracts);
            Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, postUpload, ledger, signalUser, contracts: contracts, guards: guards);
        }
    }

    public void CommitRaidSettlement(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, IAuthoritativeEventLedger ledger,
        PlayerRegistry players, SaveSnapshotPackage package, RaidSettlementSnapshotMutation mutation, Action<string> signalUser, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        EnsureNoPendingCommit();
        mutation.Validate(package.Envelope.Identity);
        mutation.Preflight(ledger);
        if (!SameVersion(snapshots.GetLatest(package.Envelope.Identity.OwnerId!, package.Envelope.Identity.ColonyId!)?.Envelope, mutation.Previous))
            throw new InvalidDataException("Raid defender snapshot changed before intent creation.");
        var intent = new SnapshotCommitIntent(8, Guid.NewGuid().ToString("N"), mutation.Previous.Identity.SnapshotId,
            mutation.Previous.NextLineageToken, package, mutation.AppliedAtUtc, null, null, null, RaidSettlement: mutation);
        Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, (_, _) => { }, ledger, signalUser, players: players);
    }

    public void CommitRaidCleanup(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, IAuthoritativeEventLedger ledger,
        PlayerRegistry players, SaveSnapshotPackage package, RaidCleanupSnapshotMutation mutation,
        DateTimeOffset acceptedAtUtc, Action<string> signalUser, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        EnsureNoPendingCommit();
        mutation.Validate(package.Envelope.Identity);
        mutation.Preflight(ledger);
        if (!SameVersion(snapshots.GetLatest(package.Envelope.Identity.OwnerId!, package.Envelope.Identity.ColonyId!)?.Envelope, mutation.Previous))
            throw new InvalidDataException("Raid attacker snapshot changed before cleanup intent creation.");
        var intent = new SnapshotCommitIntent(9, Guid.NewGuid().ToString("N"), mutation.Previous.Identity.SnapshotId,
            mutation.Previous.NextLineageToken, package, acceptedAtUtc, null, null, null, RaidCleanup: mutation);
        Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, (_, _) => { }, ledger, signalUser, players: players);
    }

    public void CommitMercenary(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        MercenaryContractRegistry contracts, MercenaryGuardContractRegistry guards, SaveSnapshotPackage package,
        LatestSnapshotRecord snapshot, MercenarySnapshotMutation mutation, Action<LatestSnapshotRecord, string?> postUpload,
        string? snapshotUploadKind = null, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        lock (mutation.Gate(contracts, guards))
        {
            EnsureNoPendingCommit();
            mutation.Validate(snapshot.Identity);
            mutation.Preflight(contracts, guards);
            if (mutation.Contract is { } contract && contracts.FindByIdempotencyKey(contract.IdempotencyKey) is not null
                || mutation.Guard is { } guard && guards.FindByIdempotencyKey(guard.IdempotencyKey) is not null)
                throw new InvalidOperationException("Use the existing mercenary contract instead of committing it again.");
            LatestSnapshotRecord? previous = snapshots.GetLatest(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!);
            var intent = new SnapshotCommitIntent(5, Guid.NewGuid().ToString("N"),
                previous?.Identity.SnapshotId, previous?.Envelope.NextLineageToken,
                package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, snapshot.AcceptedAtUtc,
                null, null, snapshotUploadKind, Mercenary: mutation);
            intent = AttachPendingOperations(intent, bank, contracts);
            Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, postUpload, contracts: contracts, guards: guards);
        }
    }

    public void CommitShop(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, ServerShopRegistry shop,
        SaveSnapshotPackage package, LatestSnapshotRecord snapshot, ShopSnapshotMutation mutation,
        Action<LatestSnapshotRecord, string?> postUpload, string? snapshotUploadKind = null, MercenaryContractRegistry? contracts = null, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        lock (shop.SnapshotCommitGate)
        {
            EnsureNoPendingCommit();
            mutation.Validate(snapshot.Identity);
            shop.ValidateSnapshotCommit(mutation);
            if (shop.FindReceipt(mutation.Receipt.IdempotencyKey) is not null)
                throw new InvalidOperationException("Use the existing purchase receipt instead of committing it again.");
            LatestSnapshotRecord? previous = snapshots.GetLatest(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!);
            var intent = new SnapshotCommitIntent(4, Guid.NewGuid().ToString("N"),
                previous?.Identity.SnapshotId, previous?.Envelope.NextLineageToken,
                package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, snapshot.AcceptedAtUtc,
                null, null, snapshotUploadKind, Shop: mutation);
            intent = AttachPendingOperations(intent, bank, contracts);
            Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, postUpload, shop: shop, contracts: contracts);
        }
    }

    public void CommitTrade(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        IAuthoritativeEventLedger ledger, SaveSnapshotPackage package, LatestSnapshotRecord snapshot,
        TradeSnapshotMutation trade, Action<LatestSnapshotRecord, string?> postUpload, Action<string> signalUser,
        Action<AuthoritativeEvent> recordTrade, string? snapshotUploadKind = null, MercenaryContractRegistry? contracts = null, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        EnsureNoPendingCommit();
        trade.Validate(snapshot.Identity);
        LedgerBatchValidation.Validate(trade.Batch, ledger.Find, ledger.FindByIdempotencyKey);
        LatestSnapshotRecord? previous = snapshots.GetLatest(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!);
        var intent = new SnapshotCommitIntent(3, Guid.NewGuid().ToString("N"),
            previous?.Identity.SnapshotId, previous?.Envelope.NextLineageToken,
            package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, snapshot.AcceptedAtUtc,
            null, null, snapshotUploadKind, Trade: trade);
        intent = AttachPendingOperations(intent, bank, contracts);
        Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, postUpload, ledger, signalUser, recordTrade, contracts: contracts);
    }

    public void CommitGift(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        IAuthoritativeEventLedger ledger, SaveSnapshotPackage package, LatestSnapshotRecord snapshot,
        AuthoritativeEvent gift, Action<LatestSnapshotRecord, string?> postUpload, Action<string> signalUser,
        string? snapshotUploadKind = null, MercenaryContractRegistry? contracts = null, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        EnsureNoPendingCommit();
        SnapshotIdentity identity = snapshot.Identity;
        if (!GiftSnapshotReplayPolicy.Matches(gift, identity.OwnerId!, identity.ColonyId, identity.SnapshotId))
            throw new ArgumentException("Gift must belong to the confirmed snapshot.");
        if (ledger.FindByIdempotencyKey(gift.IdempotencyKey) is not null)
            throw new InvalidOperationException("Gift idempotency key is already in use.");
        LatestSnapshotRecord? previous = snapshots.GetLatest(identity.OwnerId!, identity.ColonyId!);
        var intent = new SnapshotCommitIntent(2, Guid.NewGuid().ToString("N"),
            previous?.Identity.SnapshotId, previous?.Envelope.NextLineageToken,
            package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, snapshot.AcceptedAtUtc,
            null, null, snapshotUploadKind, gift);
        intent = AttachPendingOperations(intent, bank, contracts);
        Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, postUpload, ledger, signalUser, contracts: contracts);
    }

    public void CommitSupport(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        IAuthoritativeEventLedger ledger, SaveSnapshotPackage package, LatestSnapshotRecord snapshot,
        SupportSnapshotMutation mutation, Action<LatestSnapshotRecord, string?> postUpload, Action<string> signalUser,
        string? snapshotUploadKind = null, MercenaryContractRegistry? contracts = null, PreparedSnapshotPayload? preparedStoragePayload = null)
    {
        EnsureNoPendingCommit();
        mutation.Validate(snapshot.Identity);
        if (mutation.Preflight(ledger)) throw new InvalidOperationException("Use the existing support instead of committing it again.");
        var previous = snapshots.GetLatest(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!);
        var intent = new SnapshotCommitIntent(12, Guid.NewGuid().ToString("N"), previous?.Identity.SnapshotId,
            previous?.Envelope.NextLineageToken, package with { Envelope = snapshot.Envelope, Index = snapshot.Index },
            snapshot.AcceptedAtUtc, null, null, snapshotUploadKind, Support: mutation);
        intent = AttachPendingOperations(intent, bank, contracts);
        Apply(PersistNewIntent(intent, preparedStoragePayload), snapshots, bank, postUpload, ledger, signalUser, contracts: contracts);
    }

    private void EnsureNoPendingCommit()
    {
        if (pending.Count != 0) throw new InvalidOperationException("Recover pending snapshot commits before accepting new work.");
    }

    // The caller has completed business preflight under the mutation gate.
    // Validate and persist before publishing the intent or upload count; Apply only receives registered work.
    private SnapshotCommitIntent PersistNewIntent(SnapshotCommitIntent intent, PreparedSnapshotPayload? payload)
    {
        intent = intent with { PreparedStoragePayload = payload };
        journal.Write(intent);
        pending.Add(intent.Id, intent);
        if (intent.Upload is not null) Interlocked.Increment(ref pendingUploads);
        return intent;
    }

    private void Apply(SnapshotCommitIntent intent, IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        Action<LatestSnapshotRecord, string?> postUpload, IAuthoritativeEventLedger? ledger = null,
        Action<string>? signalUser = null, Action<AuthoritativeEvent>? recordTrade = null, ServerShopRegistry? shop = null,
        MercenaryContractRegistry? contracts = null, MercenaryGuardContractRegistry? guards = null, PlayerRegistry? players = null, Func<SnapshotConfirmationMutation, LatestSnapshotRecord, SnapshotConfirmationPlan>? planConfirmation = null)
    {
        if (intent.PendingOperations is { } operations)
        {
            if (operations.Mercenaries.Count > 0 && contracts is null)
                throw new InvalidOperationException("Pending mercenary confirmation requires the contract registry.");
            lock (bank.SnapshotCommitGate)
            lock (contracts?.SnapshotCommitGate ?? bank.SnapshotCommitGate)
                ApplyWithBusinessGate(intent, snapshots, bank, postUpload, ledger, signalUser, recordTrade, shop, contracts, guards, players, planConfirmation);
        }
        else ApplyWithBusinessGate(intent, snapshots, bank, postUpload, ledger, signalUser, recordTrade, shop, contracts, guards, players, planConfirmation);
    }

    private void ApplyWithBusinessGate(SnapshotCommitIntent intent, IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        Action<LatestSnapshotRecord, string?> postUpload, IAuthoritativeEventLedger? ledger,
        Action<string>? signalUser, Action<AuthoritativeEvent>? recordTrade, ServerShopRegistry? shop,
        MercenaryContractRegistry? contracts, MercenaryGuardContractRegistry? guards, PlayerRegistry? players, Func<SnapshotConfirmationMutation, LatestSnapshotRecord, SnapshotConfirmationPlan>? planConfirmation)
    {
        if (intent.Raid is not null)
        {
            if (guards is null || ledger is null) throw new InvalidOperationException("Raid recovery requires the guard registry and ledger.");
            lock (guards.SnapshotCommitGate) ApplyCore(intent, snapshots, bank, postUpload, ledger, signalUser, recordTrade, shop, contracts, guards, players, planConfirmation);
        }
        else if (intent.Shop is not null)
        {
            if (shop is null) throw new InvalidOperationException("Shop recovery requires the shop registry.");
            // Hold the shop gate across preflight, snapshot publication and business commit.
            lock (shop.SnapshotCommitGate) ApplyCore(intent, snapshots, bank, postUpload, ledger, signalUser, recordTrade, shop, contracts, guards, players, planConfirmation);
        }
        else if (intent.Mercenary is { } mercenary)
        {
            lock (mercenary.Gate(contracts, guards)) ApplyCore(intent, snapshots, bank, postUpload, ledger, signalUser, recordTrade, shop, contracts, guards, players, planConfirmation);
        }
        else ApplyCore(intent, snapshots, bank, postUpload, ledger, signalUser, recordTrade, shop, contracts, guards, players, planConfirmation);
    }

    private void ApplyCore(SnapshotCommitIntent intent, IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        Action<LatestSnapshotRecord, string?> postUpload, IAuthoritativeEventLedger? ledger,
        Action<string>? signalUser, Action<AuthoritativeEvent>? recordTrade, ServerShopRegistry? shop,
        MercenaryContractRegistry? contracts, MercenaryGuardContractRegistry? guards, PlayerRegistry? players, Func<SnapshotConfirmationMutation, LatestSnapshotRecord, SnapshotConfirmationPlan>? planConfirmation)
    {
        applying = true;
        try
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
                    journal.Write(intent, replace: true);
                    pending[intent.Id] = intent;
                }
                application.Preflight(ledger!);
                ledger!.ApplyBatch(application.Plan!.Batch);
                foreach (var item in application.Plan.AchievementEvents) recordTrade!(item);
                StoreReceipt(new SnapshotConfirmationReceipt(1, snapshot.Envelope, snapshot.AcceptedAtUtc,
                    application.Request, application.Plan.Results));
            }
            journal.Remove(intent.Id);
            pending.Remove(intent.Id);
            if (intent.Upload is not null) Interlocked.Decrement(ref pendingUploads);
        }
        finally { applying = false; }
    }

    internal static bool SameVersion(SaveSnapshotEnvelope? left, SaveSnapshotEnvelope right) => left is not null
        && left.Identity == right.Identity && left.OriginalSha256 == right.OriginalSha256
        && left.PayloadSha256 == right.PayloadSha256 && left.NextLineageToken == right.NextLineageToken;
}

public sealed record SnapshotCommitIntent(int Version, string Id, string? PreviousSnapshotId, string? PreviousLineageToken,
    SaveSnapshotPackage Package, DateTimeOffset AcceptedAtUtc, BankLoanRecord? Loan, BankDebtRecord? Debt, string? SnapshotUploadKind,
    AuthoritativeEvent? Gift = null, TradeSnapshotMutation? Trade = null, ShopSnapshotMutation? Shop = null,
    MercenarySnapshotMutation? Mercenary = null, PendingSnapshotOperations? PendingOperations = null, RaidSnapshotMutation? Raid = null,
    RaidSettlementSnapshotMutation? RaidSettlement = null, RaidCleanupSnapshotMutation? RaidCleanup = null,
    SnapshotUploadMutation? Upload = null, SupportSnapshotMutation? Support = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public PreparedSnapshotPayload? PreparedStoragePayload { get; init; }
}
