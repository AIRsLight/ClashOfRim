using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class SnapshotReceptionService(
    SnapshotTransactions transactions, SnapshotMutationCoordinator mutations, SnapshotCommitService commits,
    IColonySnapshotIndexStore snapshots, BankLoanRegistry loans, MercenaryContractRegistry mercenaries,
    SnapshotPostUploads postUploads, IAuthoritativeEventLedger ledger, IEventAchievementRecorder achievements,
    SnapshotConfirmationPlanner confirmations, MercenaryIncidentRecovery incidents)
{
    internal SnapshotReception Receive(
        string userId, string colonyId, string snapshotId, SnapshotPackageMetadataDto package,
        byte[] payload, DateTimeOffset acceptedAtUtc,
        string? confirmationOperation = null, SnapshotPostUploadExtraData? postUploadData = null,
        Func<ProtocolResponse?>? validateCommit = null, SnapshotConfirmationRequest? confirmation = null,
        Func<ProtocolResponse?>? authorizeCommit = null)
    {
        var receiver = transactions.CreateSnapshotReceiver();
        string normalizedSnapshotUploadKind = SnapshotTransactions.NormalizeSnapshotUploadKind(
            package.SnapshotUploadKind, confirmationOperation);
        if (string.Equals(normalizedSnapshotUploadKind, SnapshotUploadKinds.RaidSettlementEvidence, StringComparison.Ordinal))
        {
            return new(SnapshotUploadResult.Reject(SnapshotUploadResultKind.InvalidPayload,
                "Raid settlement evidence snapshots are only accepted by the raid settlement confirmation endpoint."));
        }
        var context = new SnapshotUploadContext(userId, colonyId, snapshotId, confirmationOperation,
            normalizedSnapshotUploadKind);
        SaveSnapshotPackage savePackage = ProtocolDtoMapper.ToSaveSnapshotPackage(package, payload);
        SnapshotUploadResult prepared = SnapshotPreparationService.Prepare(receiver, context, savePackage, acceptedAtUtc);
        if (!prepared.Accepted) return new(prepared);
        // Authorization is safe to repeat before/after recovery; business validation must
        // read recovered receipts and current state only after the recovery scope is acquired.
        using (var mutation = mutations.Enter(authorizeCommit))
        {
            if (mutation.Rejection is { } denied)
                return SnapshotReception.Denied(denied);
            if (commits.IsApplying)
                throw new InvalidOperationException("Cannot publish a nested snapshot while a commit is being applied.");
            if (validateCommit?.Invoke() is { } authFailure)
                return SnapshotReception.Denied(authFailure);
            if (postUploadData is not null)
            {
                if (confirmation is not null && commits.FindConfirmationReceipt(prepared.AcceptedSnapshot!.Envelope) is { } receipt)
                {
                    return new(confirmation.Matches(receipt.Request)
                        && prepared.AcceptedSnapshot.Envelope.OriginalSha256 == receipt.Envelope.OriginalSha256
                        && prepared.AcceptedSnapshot.Envelope.PayloadSha256 == receipt.Envelope.PayloadSha256
                        ? SnapshotUploadResult.Accept(new LatestSnapshotRecord(receipt.Envelope.Identity, receipt.Envelope,
                            prepared.AcceptedSnapshot.Index, receipt.AcceptedAtUtc), normalizedSnapshotUploadKind)
                        : SnapshotUploadResult.Reject(SnapshotUploadResultKind.SnapshotReplayDetected, "Snapshot confirmation differs from its original request."));
                }
                // A lost response must not create a new lineage token or run supplied
                // candidates again. Recovery above already used the original context.
                var latest = snapshots.GetLatest(userId, colonyId);
                if (latest?.Identity.SnapshotId == snapshotId && confirmation is null)
                {
                    return new(latest.Envelope.OriginalSha256 == prepared.AcceptedSnapshot!.Envelope.OriginalSha256
                        && latest.Envelope.PayloadSha256 == prepared.AcceptedSnapshot.Envelope.PayloadSha256
                        ? SnapshotUploadResult.Accept(latest, normalizedSnapshotUploadKind)
                        : SnapshotUploadResult.Reject(SnapshotUploadResultKind.SnapshotReplayDetected,
                            "Snapshot identity was already accepted with different content."));
                }
            }
            var operations = PendingSnapshotOperations.Capture(loans, mercenaries, prepared.AcceptedSnapshot!);
            var upload = receiver.CommitPrepared(context, savePackage, prepared,
                storeAccepted: operations.IsEmpty && postUploadData is null);
            if (postUploadData is not null && upload.Accepted && upload.AcceptedSnapshot is not null)
            {
                commits.CommitUpload(snapshots, loans, mercenaries,
                    savePackage, upload.AcceptedSnapshot, operations, upload.SnapshotUploadKind,
                    postUploadData.ConfirmationOperation, postUploadData.AchievementCandidates,
                    postUploads.AfterOrdinaryUpload, confirmation, ledger,
                    item => achievements.Record(item, item.AppliedAtUtc ?? item.CreatedAtUtc),
                    confirmations.Plan, preparedStoragePayload: upload.PreparedStoragePayload);
                return new(upload);
            }
            if (!upload.Accepted || upload.AcceptedSnapshot is null || operations.IsEmpty) return new(upload);
            commits.CommitPendingOperations(snapshots, loans, mercenaries,
                savePackage, upload.AcceptedSnapshot, operations, upload.SnapshotUploadKind, preparedStoragePayload: upload.PreparedStoragePayload);
            incidents.Recover();
            return new(upload);
        }
    }
}
