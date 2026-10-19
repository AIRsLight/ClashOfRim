using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal sealed class ClientSnapshotUploadProtocol : IClientSnapshotTransport
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;
    internal ClientSnapshotUploadProtocol(ClientProtocolTransport transport, Func<string> notConfigured)
    { this.transport = transport; this.notConfigured = notConfigured; }

    public Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(
        ClashOfRimClientNetworkContext context, string idempotencyKey, ModSnapshotPackageMetadataDto package,
        byte[] payload, string? confirmationOperation = null, IReadOnlyList<ModSnapshotAchievementCandidateDto>? achievements = null,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured) return Task.FromResult(ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Failed("NotConfigured", notConfigured()));
        package.SnapshotUploadKind = UploadKind(package.SnapshotUploadKind, confirmationOperation);
        return transport.ForContext(context).PostSnapshotMultipartAsync<ModUploadSnapshotRequestDto, ModUploadSnapshotResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.UploadSnapshot).Route,
            new()
            {
                IdempotencyKey = idempotencyKey, UserId = context.UserId, ColonyId = context.ColonyId,
                SnapshotId = package.SnapshotId, Package = package, AuthToken = context.AuthToken,
                ConfirmationOperation = confirmationOperation,
                AchievementCandidates = achievements?.ToList() ?? new()
            }, payload, cancellationToken);
    }

    private static string UploadKind(string? explicitKind, string? operation)
    {
        if (!string.IsNullOrWhiteSpace(explicitKind)) return explicitKind!;
        return operation switch
        {
            ModSnapshotUploadKinds.ColonyRelocation => ModSnapshotUploadKinds.ColonyRelocation,
            ModSnapshotUploadKinds.EndgameAchievement => ModSnapshotUploadKinds.EndgameAchievement,
            _ => ModSnapshotUploadKinds.ManualUpload
        };
    }
}
