namespace AIRsLight.ClashOfRim.Protocol;

public sealed class ProtocolIdentity
{
    public ProtocolIdentity(string userId, string? colonyId, string? snapshotId)
    {
        UserId = userId;
        ColonyId = colonyId;
        SnapshotId = snapshotId;
    }

    public string UserId { get; }

    public string? ColonyId { get; }

    public string? SnapshotId { get; }
}
