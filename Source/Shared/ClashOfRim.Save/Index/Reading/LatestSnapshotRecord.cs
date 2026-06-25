namespace AIRsLight.ClashOfRim.Save;

public sealed record LatestSnapshotRecord(
    SnapshotIdentity Identity,
    SaveSnapshotEnvelope Envelope,
    SaveSnapshotIndex Index,
    DateTimeOffset AcceptedAtUtc);
