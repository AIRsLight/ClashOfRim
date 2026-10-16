namespace AIRsLight.ClashOfRim.Network;

// The endpoint owns recovery, including rejecting retired commands without recovery.
// Attach only when its execution paths already perform the required recovery themselves;
// the middleware must not preempt the handler's validation and authorization order.
// Other routes retain the bootstrap fallback until their recovery ownership is migrated.
internal sealed class EndpointSnapshotRecovery
{
    public static readonly EndpointSnapshotRecovery Instance = new();
    private EndpointSnapshotRecovery() { }
}
