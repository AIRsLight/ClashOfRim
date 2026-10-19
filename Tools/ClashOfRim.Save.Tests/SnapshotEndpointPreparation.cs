using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.IO.Compression;
using System.Xml.Linq;

internal static class SnapshotEndpointPreparation
{
    public static SaveSnapshotPackage Prepare(IColonySnapshotIndexStore snapshots, Func<string, SaveSnapshotPackage> fixture,
        ProtocolIdentity owner, string snapshotId)
    {
        var baseline = fixture("before");
        baseline = baseline with { Envelope = baseline.Envelope with { Identity = new SnapshotIdentity(owner.UserId, owner.ColonyId!, "before") } };
        var accepted = new SnapshotUploadReceiver(snapshots).Receive(new SnapshotUploadContext(owner.UserId, owner.ColonyId!, "before"),
            baseline, DateTimeOffset.UtcNow).AcceptedSnapshot!;
        using var compressed = new MemoryStream(baseline.Payload);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        var document = XDocument.Load(gzip);
        document.Root!.Element("game")!.Add(new XElement("components", new XElement("li",
            new XAttribute("Class", "AIRsLight.ClashOfRim.ClashOfRimGameComponent"),
            new XElement("clashOfRimLineageSnapshotId", "before"), new XElement("clashOfRimLineageToken", accepted.Envelope.NextLineageToken))));
        string path = Path.Combine(Path.GetTempPath(), "cor-hire-child-" + Guid.NewGuid().ToString("N") + ".rws");
        try
        {
            document.Save(path);
            return SaveSnapshotPackageBuilder.FromFile(path, new SnapshotIdentity(owner.UserId, owner.ColonyId!, snapshotId), DateTimeOffset.UtcNow);
        }
        finally { File.Delete(path); }
    }

}
