using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Raids;

public static class RaidAttackerLossSnapshotConfirmationQueue
{
    private static readonly List<RaidAttackerLossSnapshotConfirmationRequest> PendingRequests = new();

    public static int PendingCount => PendingRequests.Count;

    public static IReadOnlyList<RaidAttackerLossSnapshotConfirmationRequest> PendingForReading =>
        PendingRequests.ToList();

    public static void Enqueue(RaidAttackerLossSnapshotConfirmationRequest request)
    {
        PendingRequests.Add(request);
    }

    public static void Clear()
    {
        PendingRequests.Clear();
    }
}
