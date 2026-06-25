namespace AIRsLight.ClashOfRim.Save;

public enum SnapshotUploadResultKind
{
    Accepted,
    MissingIdentity,
    IdentityMismatch,
    MissingHash,
    PayloadHashMismatch,
    OriginalHashMismatch,
    MissingRimWorldVersion,
    IncompatibleRimWorldVersion,
    UnsupportedPayloadEncoding,
    SnapshotReplayDetected,
    SnapshotLineageMismatch,
    SnapshotTimeRegression,
    SnapshotContinuityMismatch,
    InvalidPayload
}
