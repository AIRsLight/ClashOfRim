using AIRsLight.ClashOfRim.Network.Modules.Raids;

namespace AIRsLight.ClashOfRim.Network;

// The public plugin contract retains its context; domain scheduling receives only request data.
internal static class RaidEvidenceRequestAdapter
{
    internal static RaidEvidenceSchedule From(RaidSettlementScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.Context;
        ArgumentNullException.ThrowIfNull(context);
        return new(request.RaidEventId, context.Snapshot, SnapshotPostUploadJobRegistry.Input(context),
            request.EvidencePayload, request.Origin, request.ClientApplicationResult, request.RequestHash);
    }
}
