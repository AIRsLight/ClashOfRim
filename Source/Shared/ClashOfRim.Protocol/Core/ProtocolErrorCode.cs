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
    LossNotReflected,
    AccountNotFound,
    // The supplied credential/session can no longer authenticate a request.
    // Ordinary permission or request-identity denials must retain their own code.
    SessionUnavailable
}
