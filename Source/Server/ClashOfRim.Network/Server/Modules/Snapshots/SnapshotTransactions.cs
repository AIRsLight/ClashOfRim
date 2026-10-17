using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class SnapshotTransactions(
    IColonySnapshotIndexStore snapshots, SnapshotUploadPolicy policy,
    ServerPluginRegistry plugins, CompatibilityBaselineRegistry baseline)
{
    internal SnapshotUploadReceiver CreateSnapshotReceiver()
    {
        return new SnapshotUploadReceiver(
            snapshots,
            policy,
            plugins.ActiveSaveIndexExtensions(baseline.Current),
            plugins
                .ActiveWorldObjectClassifiers(baseline.Current)
                .Select(classifier => (Func<WorldObjectSummary, bool>)classifier.IsPlayerColonyAnchor)
                .ToList());
    }

    internal SnapshotUploadResult PrepareTransactionSnapshot(string userId, string colonyId,
        string snapshotId, SnapshotPackageMetadataDto metadata, byte[] payload, DateTimeOffset at)
    {
        string? kind = NormalizeSnapshotUploadKind(metadata.SnapshotUploadKind);
        if (kind == SnapshotUploadKinds.RaidSettlementEvidence)
            return SnapshotUploadResult.Reject(SnapshotUploadResultKind.InvalidPayload,
                "Raid settlement evidence snapshots are only accepted by the raid settlement confirmation endpoint.");
        return SnapshotPreparationService.Prepare(CreateSnapshotReceiver(),
            new SnapshotUploadContext(userId, colonyId, snapshotId, SnapshotUploadKind: kind),
            ProtocolDtoMapper.ToSaveSnapshotPackage(metadata, payload), at);
    }

    internal static string NormalizeSnapshotUploadKind(string? snapshotUploadKind, string? confirmationOperation = null)
    {
        if (!string.IsNullOrWhiteSpace(snapshotUploadKind))
        {
            return snapshotUploadKind!;
        }

        if (string.Equals(confirmationOperation, SnapshotConfirmationOperations.ColonyRelocation, StringComparison.Ordinal))
        {
            return SnapshotUploadKinds.ColonyRelocation;
        }

        if (string.Equals(confirmationOperation, SnapshotConfirmationOperations.EndgameAchievement, StringComparison.Ordinal))
        {
            return SnapshotUploadKinds.EndgameAchievement;
        }

        return SnapshotUploadKinds.ManualUpload;
    }

    internal string? FindSnapshotNextLineageToken(
        string userId,
        string colonyId,
        string? snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return null;
        }

        LatestSnapshotRecord? latest = snapshots.GetLatest(userId, colonyId);
        return string.Equals(latest?.Identity.SnapshotId, snapshotId, StringComparison.Ordinal)
            ? latest?.Envelope.NextLineageToken
            : null;
    }

    internal static ProtocolResponse ToProtocolResponse(SnapshotUploadResult upload)
    {
        return upload.Accepted
            ? ProtocolResponse.Ok(upload.Message)
            : ProtocolResponse.Reject(ToProtocolError(upload.Kind), upload.Message);
    }

    internal static ProtocolErrorCode ToProtocolError(SnapshotUploadResultKind kind)
    {
        return kind switch
        {
            SnapshotUploadResultKind.Accepted => ProtocolErrorCode.None,
            SnapshotUploadResultKind.IdentityMismatch => ProtocolErrorCode.IdentityMismatch,
            SnapshotUploadResultKind.MissingIdentity => ProtocolErrorCode.IdentityMismatch,
            SnapshotUploadResultKind.IncompatibleRimWorldVersion => ProtocolErrorCode.ServerRejected,
            SnapshotUploadResultKind.SnapshotReplayDetected => ProtocolErrorCode.ServerRejected,
            SnapshotUploadResultKind.SnapshotLineageMismatch => ProtocolErrorCode.ServerRejected,
            SnapshotUploadResultKind.SnapshotTimeRegression => ProtocolErrorCode.ServerRejected,
            SnapshotUploadResultKind.SnapshotContinuityMismatch => ProtocolErrorCode.ServerRejected,
            _ => ProtocolErrorCode.ValidationFailed
        };
    }
}
