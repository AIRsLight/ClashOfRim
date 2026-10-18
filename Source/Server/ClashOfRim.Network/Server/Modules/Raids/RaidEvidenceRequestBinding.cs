using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal static class RaidEvidenceRequestBinding
{
    // A renewed authenticated session may retry the original confirmation. Credentials are not business data.
    internal static string Hash(ConfirmEventApplicationMetadataRequest request, byte[] bytes) => SnapshotRequestBinding.Hash(
        new ConfirmEventApplicationMetadataRequest(request.IdempotencyKey, request.EventId, request.SourceEventId,
            request.UserId, request.ColonyId, request.BaseSnapshotId, request.ConfirmedSnapshot, request.ClientApplicationResult), bytes);
}
