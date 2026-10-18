namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal enum ClientLifecycleEventKind
{
    InitialWorldSessionPrepared,
    LoggedIn,
    ColonyAbandoned
}

internal readonly record struct ClientLifecycleEvent(
    ClientLifecycleEventKind Kind,
    string UserId,
    string ColonyId,
    string? CurrentSnapshotId,
    string? SessionId,
    DateTimeOffset OccurredAtUtc)
{
    public static ClientLifecycleEvent InitialWorldSessionPrepared(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        DateTimeOffset occurredAtUtc)
    {
        return new ClientLifecycleEvent(
            ClientLifecycleEventKind.InitialWorldSessionPrepared,
            userId,
            colonyId,
            currentSnapshotId,
            SessionId: null,
            occurredAtUtc);
    }

    public static ClientLifecycleEvent LoggedIn(
        string userId,
        string colonyId,
        string sessionId,
        string? currentSnapshotId,
        DateTimeOffset occurredAtUtc)
    {
        return new ClientLifecycleEvent(
            ClientLifecycleEventKind.LoggedIn,
            userId,
            colonyId,
            currentSnapshotId,
            sessionId,
            occurredAtUtc);
    }

    public static ClientLifecycleEvent ColonyAbandoned(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        DateTimeOffset occurredAtUtc)
    {
        return new ClientLifecycleEvent(
            ClientLifecycleEventKind.ColonyAbandoned,
            userId,
            colonyId,
            currentSnapshotId,
            SessionId: null,
            occurredAtUtc);
    }
}
