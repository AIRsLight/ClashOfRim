using System.Reflection;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class DownloadCleanupBaselineTests
{
    public static void Run(SaveSnapshotPackage captured)
    {
        MethodInfo cleanup = typeof(ClashOfRimNetworkServer).GetMethod("CleanupSettledRaidBattleSnapshotBeforeDownload",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (string change in new[] { "unchanged", "snapshot", "hash", "token", "removed" })
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-download-cleanup-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileColonySnapshotIndexStore(root);
                var state = new ClashOfRimNetworkState(snapshotStore: store);
                var identity = captured.Envelope.Identity;
                var raid = AuthoritativeEventFactory.Create(ServerEventType.Raid,
                    new EventParty(identity.OwnerId!, identity.ColonyId), new EventParty("defender", "defender-colony"),
                    "finished-raid", false, new RaidEventPayload("defender-snapshot", null, DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch.AddSeconds(1), null), DateTimeOffset.UnixEpoch) with
                {
                    EventId = "raid-timeout-001", Status = ServerEventStatus.AppliedToSnapshot
                };
                state.Ledger.Append(raid);
                store.StoreLatest(captured, captured.Index, DateTimeOffset.UtcNow);
                SaveSnapshotEnvelope newerEnvelope = change switch
                {
                    "snapshot" => captured.Envelope with { Identity = identity with { SnapshotId = "newer-snapshot" } },
                    "hash" => captured.Envelope with { OriginalSha256 = "changed-content" },
                    "token" => captured.Envelope with { NextLineageToken = "changed-token" },
                    _ => captured.Envelope
                };
                // Model publication between download capture and its cleanup side effect.
                if (change == "removed") store.RemoveLatest(identity.OwnerId!, identity.ColonyId!);
                else if (change != "unchanged")
                    store.StoreLatest(captured with { Envelope = newerEnvelope }, captured.Index, DateTimeOffset.UtcNow);
                LatestSnapshotRecord? before = store.GetLatest(identity.OwnerId!, identity.ColonyId!);
                var returned = (SaveSnapshotPackage)cleanup.Invoke(null,
                    new object[] { state, store, captured, identity.OwnerId!, identity.ColonyId!, DateTimeOffset.UtcNow })!;
                LatestSnapshotRecord? after = store.GetLatest(identity.OwnerId!, identity.ColonyId!);
                if (change == "unchanged")
                {
                    if (after?.Identity.SnapshotId == identity.SnapshotId || returned.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent"))
                        throw new Exception("Current settled raid download was not cleaned.");
                }
                else if (after != before || !ReferenceEquals(returned, captured) || state.Ledger.ListAll().Count != 1)
                    throw new Exception("Stale download cleanup overwrote a changed/removed snapshot or emitted loss events: " + change);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }
    }
}
