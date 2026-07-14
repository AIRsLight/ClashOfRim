using System;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal enum SessionReconnectBlockReason
{
    None,
    NotInPlayableSession,
    MissingIdentity,
    SynchronizationBusy,
    AtomicMutationPending
}

internal static class SessionReconnectSafetyPolicy
{
    public static SessionReconnectBlockReason EvaluateLocalState(
        bool isPlaying,
        bool hasGame,
        bool isConfigured,
        string? userId,
        string? colonyId,
        string? currentSnapshotId,
        bool synchronizationBusy,
        bool atomicMutationPending)
    {
        if (!isPlaying || !hasGame)
        {
            return SessionReconnectBlockReason.NotInPlayableSession;
        }

        if (!isConfigured
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(colonyId)
            || string.IsNullOrWhiteSpace(currentSnapshotId))
        {
            return SessionReconnectBlockReason.MissingIdentity;
        }

        if (atomicMutationPending)
        {
            return SessionReconnectBlockReason.AtomicMutationPending;
        }

        return synchronizationBusy
            ? SessionReconnectBlockReason.SynchronizationBusy
            : SessionReconnectBlockReason.None;
    }

    public static bool MatchesAuthoritativeSnapshot(
        string expectedColonyId,
        string expectedSnapshotId,
        bool hasExistingColony,
        string? assignedColonyId,
        string? latestSnapshotId)
    {
        return hasExistingColony
            && string.Equals(expectedColonyId, assignedColonyId, StringComparison.Ordinal)
            && string.Equals(expectedSnapshotId, latestSnapshotId, StringComparison.Ordinal);
    }
}
