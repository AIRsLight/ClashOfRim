namespace AIRsLight.ClashOfRim.Save;

public sealed record SaveSnapshotPackage(
    SaveSnapshotEnvelope Envelope,
    byte[] Payload,
    SaveSnapshotIndex Index);
