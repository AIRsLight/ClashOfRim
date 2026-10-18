using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

/// <summary>A write-ahead intent bridges snapshot files and business records. Call under the snapshot mutation gate.</summary>
public sealed class SnapshotCommitService : ISnapshotCommitCheckpoint, ISnapshotCommitPreparationOwner
{
    private readonly SnapshotConfirmationReceipts receipts;
    private readonly SnapshotCommitJournal journal;
    private readonly Dictionary<string, SnapshotCommitIntent> pending = new(StringComparer.Ordinal);
    private bool applying;
    internal bool HasPending => pending.Count != 0;
    private int pendingUploads;
    internal bool HasPendingUploads => Volatile.Read(ref pendingUploads) != 0;
    internal bool IsApplying => applying;

    public SnapshotCommitService(string? directory = null)
    {
        journal = new SnapshotCommitJournal(directory);
        receipts = new SnapshotConfirmationReceipts(directory);
        foreach (SnapshotCommitIntent intent in journal.ReadPending())
        {
            pending.Add(intent.Id, intent);
            if (intent.Upload is not null) pendingUploads++;
        }
    }

    public static void ValidateDirectory(string directory)
    {
        SnapshotCommitJournal.ValidateDirectory(directory);
        SnapshotConfirmationReceipts.ValidateDirectory(directory);
    }

    internal SnapshotConfirmationReceipt? FindConfirmationReceipt(SaveSnapshotEnvelope envelope) => receipts.Find(envelope);

    internal SnapshotConfirmationReceipt? FindConfirmationReceipt(SnapshotIdentity identity, string originalHash, string payloadHash) =>
        receipts.Find(identity, originalHash, payloadHash);

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
                if (intent != original) PersistCheckpoint(intent);
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
            new SnapshotCommitApplication(snapshots, bank, postUpload, ledger, signalUser, recordTrade,
                shop, contracts, guards, players, planConfirmation, receipts, this).Apply(intent);
            journal.Remove(intent.Id);
            pending.Remove(intent.Id);
            if (intent.Upload is not null) Interlocked.Decrement(ref pendingUploads);
        }
        finally { applying = false; }
    }

    internal ISnapshotRecoveryPreparation CaptureRecoveryPreparation(IColonySnapshotIndexStore snapshots) =>
        new SnapshotCommitRecoveryPreparation(this, snapshots, pending.Values.ToArray());

    bool ISnapshotCommitPreparationOwner.IsCurrent(IReadOnlyList<SnapshotCommitIntent> captured) =>
        !applying && pending.Count == captured.Count
        && captured.All(intent => pending.TryGetValue(intent.Id, out var current) && ReferenceEquals(current, intent));

    void ISnapshotCommitPreparationOwner.InstallPreparedPayload(SnapshotCommitIntent intent, PreparedSnapshotPayload payload) =>
        pending[intent.Id] = intent with { PreparedStoragePayload = payload };

    void ISnapshotCommitCheckpoint.Persist(SnapshotCommitIntent intent) => PersistCheckpoint(intent);

    private void PersistCheckpoint(SnapshotCommitIntent intent)
    {
        journal.Write(intent, replace: true);
        pending[intent.Id] = intent;
    }

    internal static bool SameVersion(SaveSnapshotEnvelope? left, SaveSnapshotEnvelope right) =>
        SnapshotCommitApplication.SameVersion(left, right);
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
