namespace AIRsLight.ClashOfRim.Protocol;

public enum ProtocolErrorCode
{
    None,
    Unauthorized,
    IncompatibleProtocolVersion,
    IdentityMismatch,
    SnapshotMismatch,
    EventNotFound,
    DuplicateRequest,
    ValidationFailed,
    ServerRejected,
    Conflict,
    LossNotReflected
}
