using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Xml.Linq;

internal sealed class RaidEvidenceModuleTestContext : IDisposable
{
    private readonly bool preparedArtifacts;
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "cor-evidence-module-" + Guid.NewGuid().ToString("N"));
    internal readonly object Gate = new();
    internal readonly EconomicModuleTestContext.RecoveryProbe Recovery = new();
    internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
    internal readonly SnapshotMutationCoordinator Mutations;
    internal readonly RaidSettlementTestData Data;
    internal readonly SaveSnapshotPackage Before;
    internal readonly RaidEvidenceSchedule Request;
    internal FileColonySnapshotIndexStore Snapshots = null!;
    internal ISnapshotPostUploadArtifactStore Artifacts = null!;
    internal SnapshotPostUploadJobRegistry Jobs = null!;
    internal PlayerRegistry Players = null!;
    internal RaidEvidenceScheduling Scheduling = null!;
    internal string ArtifactId => RaidEvidenceRules.BuildArtifactId(Data.Raid.EventId, "evidence");

    internal RaidEvidenceModuleTestContext(bool preparedArtifacts = true)
    {
        this.preparedArtifacts = preparedArtifacts;
        Directory.CreateDirectory(Root);
        Data = new(Root);
        Mutations = new(Gate, Recovery);
        Before = Data.Original with { Envelope = Data.Original.Envelope with
            { Identity = new("attacker", "attacker-colony", "attack"), NextLineageToken = "attack-token" } };
        string path = Path.Combine(Root, "evidence.rws");
        var document = XDocument.Load(path);
        document.Root!.Element("game")!.Add(new XElement("components",
            new XElement("li", new XAttribute("Class", "AIRsLight.ClashOfRim.ClashOfRimGameComponent"),
                new XElement("clashOfRimLineageSnapshotId", "attack"), new XElement("clashOfRimLineageToken", "attack-token"))));
        document.Save(path);
        var evidence = SaveSnapshotPackageBuilder.FromFile(path, Data.Evidence.Envelope.Identity, Data.Now);
        evidence = evidence with { Envelope = evidence.Envelope with
            { PreviousSnapshotId = "attack", LineageToken = "attack-token", NextLineageToken = "evidence-token" } };
        Request = new(Data.Raid.EventId, new(evidence.Envelope.Identity, evidence.Envelope, evidence.Index, Data.Now),
            new(SnapshotPostUploadKind.RaidSettlementEvidence, "attacker", "attacker-colony", "session", "evidence", Data.Now),
            evidence.Payload, RaidSettlementOrigin.OnlineEvidence, "Applied", "request-binding");
        Ledger.Append(Data.Raid);
        Open();
        Snapshots.StoreLatest(Data.Original, Data.Original.Index, Data.Now);
        Snapshots.StoreLatest(Before, Before.Index, Data.Now);
        Players.RecordLatestSnapshotReference("attacker", "attacker-colony", "attack", Data.Now);
    }

    internal void Open()
    {
        Snapshots = new(Path.Combine(Root, "snapshots"));
        Artifacts = preparedArtifacts ? new FileSnapshotPostUploadArtifactStore(Path.Combine(Root, "artifacts"))
            : new InMemorySnapshotPostUploadArtifactStore();
        Jobs = new(new SqliteSnapshotPostUploadJobStore(Path.Combine(Root, "jobs.sqlite")));
        Players = new(Path.Combine(Root, "players.json"));
        Scheduling = NewScheduling();
    }

    internal RaidEvidenceScheduling NewScheduling() => new(Ledger, Snapshots, Artifacts, Jobs, Players, Mutations);
    internal T WithMutation<T>(Func<T> action)
    {
        using var mutation = Mutations.EnterUnrecovered();
        lock (Jobs.SnapshotCommitGate) return action();
    }
    internal void WithMutation(Action action) => WithMutation(() => { action(); return true; });

    internal SnapshotPostUploadJobRecord SeedPrepared()
    {
        var payload = Data.Payload with { EvidenceArtifactId = ArtifactId, RequestHash = Request.RequestHash,
            Publication = new(Before.Envelope, Request.Snapshot.Envelope) };
        Artifacts.Store(ArtifactId, new(Request.Snapshot.Envelope, Request.EvidencePayload, Request.Snapshot.Index));
        return Jobs.EnqueuePrepared("raid-settlement:" + Data.Raid.EventId, RaidSettlementWork.ProcessorId, Request.Job, payload.Serialize());
    }

    internal void CheckUnpublished() => Check(Jobs.ListAll().Count == 0 && !Artifacts.Exists(ArtifactId)
        && Snapshots.GetLatest("attacker", "attacker-colony")!.Identity.SnapshotId == "attack"
        && Players.FindByUserId("attacker")!.CurrentSnapshotId == "attack", "Preparation or rejection published evidence prematurely.");

    internal void CheckPublished(SnapshotPostUploadJobRecord job) => Check(job.State == SnapshotPostUploadJobState.Ready
        && job.Kind == Request.Job.Kind && job.SessionId == Request.Job.SessionId && job.OccurredAtUtc == Data.Now
        && Artifacts.Read(ArtifactId)!.Envelope == Request.Snapshot.Envelope
        && Snapshots.GetLatest("attacker", "attacker-colony")!.Envelope == Request.Snapshot.Envelope
        && Players.FindByUserId("attacker")!.CurrentSnapshotId == "evidence"
        && Snapshots.GetLatest("defender", "defender-colony")!.Identity.SnapshotId == "before" && Ledger.ListAll().Count == 1,
        "Evidence publication lost metadata or performed settlement early.");

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
