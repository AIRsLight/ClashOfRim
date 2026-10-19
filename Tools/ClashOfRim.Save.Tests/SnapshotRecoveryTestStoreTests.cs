using AIRsLight.ClashOfRim.Save;
using static SnapshotTestAssertions;

internal static class SnapshotRecoveryTestStoreTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        var package = factory("stored");
        foreach (bool prepared in new[] { false, true })
        foreach (string fault in new[] { "none", "before", "after" })
            SqliteTestDirectory.With("cor-store-probe-", root =>
            {
                var store = new SnapshotRecoveryTestStore(root) { Gate = new object(),
                    FailPublication = fault == "before", FailAfterPublication = fault == "after" };
                var payload = prepared ? store.PreparePayload(package.Payload) : null;
                void Publish()
                {
                    if (prepared) store.StorePrepared(package, package.Index, DateTimeOffset.UnixEpoch, payload!);
                    else store.StoreLatest(package, package.Index, DateTimeOffset.UnixEpoch);
                }
                string? error = null;
                try { Publish(); } catch (Exception ex) { error = ex.Message; }
                Check(error == "Snapshot recovery scope did not retain the mutation gate for publication."
                    && store.WriteAttempts == 0 && store.ListLatest().Count == 0, "Store probe wrote outside its gate.");
                lock (store.Gate)
                {
                    if (fault == "none") Publish();
                    else Expect<IOException>(Publish, "Store probe did not interrupt the intended publication boundary.");
                }
                bool published = fault != "before";
                int successes = published ? 1 : 0;
                string owner = package.Envelope.Identity.OwnerId!, colony = package.Envelope.Identity.ColonyId!;
                Check((new FileColonySnapshotIndexStore(root).GetLatest(owner, colony) is not null) == published
                    && store.WriteAttempts == 1 && store.PreparedWriteAttempts == (prepared ? 1 : 0)
                    && store.Writes == successes && store.PreparedWrites == (prepared ? successes : 0)
                    && store.RawWrites == (prepared ? 0 : successes) && store.WritesByOwner.GetValueOrDefault(owner) == successes,
                    "Store probe confused attempted and durable publication: " + prepared + "/" + fault);
                store.Reset();
                Check(store.WriteAttempts == 0 && store.PreparedWriteAttempts == 0 && store.Preparations == 0
                    && store.Writes == 0 && store.WritesByOwner.Count == 0
                    && store.FailPublication == (fault == "before") && store.FailAfterPublication == (fault == "after")
                    && (store.GetLatest(owner, colony) is not null) == published, "Reset changed persisted data or fault configuration.");

                store.Inner.StoreLatest(package, package.Index, DateTimeOffset.UnixEpoch);
                var replacement = package with { Envelope = package.Envelope with { Identity = package.Envelope.Identity with { SnapshotId = "replacement" } } };
                string? readOwner = null;
                store.BeforeRead = name =>
                {
                    readOwner = name;
                    var beforeRead = package with { Envelope = package.Envelope with { Identity = package.Envelope.Identity with { SnapshotId = "before-read" } } };
                    store.Inner.StoreLatest(beforeRead, beforeRead.Index, DateTimeOffset.UnixEpoch);
                };
                store.AfterRead = () => store.Inner.StoreLatest(replacement, replacement.Index, DateTimeOffset.UnixEpoch);
                var captured = store.GetLatestPackage(owner, colony);
                Check(readOwner == owner && captured!.Envelope.Identity.SnapshotId == "before-read" && store.GetLatest(owner, colony)!.Identity.SnapshotId == "replacement",
                    "Read probe returned a later snapshot instead of its captured package.");
                store.BeforeRead = null; store.AfterRead = null; store.ReadOverride = package;
                Check(ReferenceEquals(store.GetLatestPackage(owner, colony), package) && store.Writes == 0,
                    "Read override was republished or replaced by stored content.");
            });
    }
}
