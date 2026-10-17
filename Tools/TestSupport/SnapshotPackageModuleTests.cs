using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Snapshots;

internal static class SnapshotPackageModuleTests
{
    internal static readonly DateTime Timestamp = new(2026, 10, 17, 1, 2, 3, DateTimeKind.Utc);
    internal static byte[] Save(string version = "1.7.1234 rev5", string ticks = "12345", bool lineage = true) => Encoding.UTF8.GetBytes(
        "<savegame><meta><gameVersion> " + version + " </gameVersion></meta><game><tickManager><ticksGame>" + ticks + "</ticksGame></tickManager>"
        + "<components>" + (lineage ? "<li Class=\"AIRsLight.ClashOfRim.ClashOfRimGameComponent, AIRsLight.ClashOfRim\"><clashOfRimLineageSnapshotId> parent </clashOfRimLineageSnapshotId><clashOfRimLineageToken> token </clashOfRimLineageToken></li>" : "")
        + "</components><label>中文 à İzmir</label></game></savegame>");

    internal static void Run()
    {
        AchievementCleanup();
        byte[] original = Save();
        byte[] unchanged = original.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        foreach (string language in new[] { "en-US", "fr-FR", "tr-TR", "ar-SA", "th-TH" })
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                var result = ClientSnapshotPackageBuilder.FromSaveBytes(original, "source.rws", "owner", " colony/name ", Timestamp);
                Require(result.Success && result.ErrorKind is null && result.ErrorCode is null && result.Message is null, "A valid save failed: " + result.Message);
                var package = result.Package!;
                Require(package.SnapshotId == "colony-name-20261017010203-" + hash[..12], "Snapshot identity depends on the current calendar: " + language + ": " + package.SnapshotId);
                Require(package.OwnerId == "owner" && package.ColonyId == " colony/name " && package.RimWorldVersion == "1.7.1234 rev5"
                    && package.PackageVersion == "clash-of-rim-snapshot-v1" && package.PayloadEncoding == "GzipRws"
                    && package.GameTicks == 12345 && package.PreviousSnapshotId == "parent" && package.LineageToken == "token"
                    && package.OriginalSaveBytes == original.LongLength && package.OriginalSha256 == hash && result.SourcePath == "source.rws",
                    "Snapshot metadata, lineage or source contract changed.");
                Require(package.PayloadBytes == result.Payload!.LongLength
                    && package.PayloadSha256 == Convert.ToHexString(SHA256.HashData(result.Payload)).ToLowerInvariant(), "Payload digest or size is incorrect.");
                using var decompressed = new GZipStream(new MemoryStream(result.Payload), CompressionMode.Decompress);
                using var target = new MemoryStream(); decompressed.CopyTo(target);
                Require(target.ToArray().SequenceEqual(original) && result.OriginalPayload!.SequenceEqual(original) && original.SequenceEqual(unchanged),
                    "Packaging changed the captured save bytes.");
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        CheckFailure(Array.Empty<byte>(), "", "colony", SnapshotPackageError.MissingIdentity, "MissingIdentity");
        CheckFailure(original, "owner", " ", SnapshotPackageError.MissingIdentity, "MissingIdentity");
        CheckFailure(null!, "owner", "colony", SnapshotPackageError.MissingSavePayload, "MissingSavePayload");
        CheckFailure(Array.Empty<byte>(), "owner", "colony", SnapshotPackageError.MissingSavePayload, "MissingSavePayload");
        CheckFailure(Save(" "), "owner", "colony", SnapshotPackageError.MissingRimWorldVersion, "MissingRimWorldVersion");
        CheckFailure(Encoding.UTF8.GetBytes("<savegame><game/></savegame>"), "owner", "colony", SnapshotPackageError.MissingRimWorldVersion, "MissingRimWorldVersion");
        foreach (string invalid in new[] { "<savegame>", "<!DOCTYPE savegame [<!ENTITY version '1.7'>]><savegame><gameVersion>&version;</gameVersion></savegame>" })
        {
            var result = ClientSnapshotPackageBuilder.FromSaveBytes(Encoding.UTF8.GetBytes(invalid), "invalid", "owner", "colony", Timestamp);
            Require(!result.Success && result.ErrorCode == "XmlException" && result.ErrorKind is null && !string.IsNullOrWhiteSpace(result.Message)
                && result.Package is null && result.Payload is null, "Malformed/prohibited XML did not remain a diagnostic failure.");
        }
        var optional = ClientSnapshotPackageBuilder.FromSaveBytes(Save(ticks: "invalid", lineage: false), "optional", "owner", "colony", Timestamp);
        Require(optional.Success && optional.Package!.GameTicks is null && optional.Package.PreviousSnapshotId is null && optional.Package.LineageToken is null,
            "Optional legacy save fields became required.");
    }

    private static void AchievementCleanup()
    {
        byte[] withoutQueue = Save();
        Require(ReferenceEquals(withoutQueue, ClientSnapshotSaveData.RemovePendingAchievementQueue(withoutQueue, out var diagnostic))
            && diagnostic is null, "An absent achievement queue rewrote the payload.");
        var document = XDocument.Parse(Encoding.UTF8.GetString(withoutQueue));
        XElement component = ClientSnapshotSaveData.FindComponent(document)!;
        component.Add(new XElement("clashOfRimPendingAchievementCandidates", new XElement("li", "submitted")));
        component.Parent!.Add(new XElement("li", new XAttribute("Class", "Foreign.Component"),
            new XElement("clashOfRimPendingAchievementCandidates", "foreign")));
        byte[] queued = Encoding.UTF8.GetBytes(document.ToString());
        byte[] cleaned = ClientSnapshotSaveData.RemovePendingAchievementQueue(queued, out diagnostic);
        using var stream = new MemoryStream(cleaned);
        var result = XDocument.Load(stream);
        Require(diagnostic is null && ClientSnapshotSaveData.FindComponent(result)!.Element("clashOfRimPendingAchievementCandidates") is null
            && result.Descendants("clashOfRimPendingAchievementCandidates").Single().Value == "foreign"
            && result.Descendants("label").Single().Value == "中文 à İzmir", "Achievement cleanup changed unrelated state.");
        var package = ClientSnapshotPackageBuilder.FromSaveBytes(cleaned, "cleaned", "owner", "colony", Timestamp);
        Require(package.Success && package.Package!.PreviousSnapshotId == "parent" && package.Package.LineageToken == "token",
            "Queue cleanup removed lineage.");
        byte[] broken = Encoding.UTF8.GetBytes("<savegame>");
        Require(ReferenceEquals(broken, ClientSnapshotSaveData.RemovePendingAchievementQueue(broken, out diagnostic))
            && !string.IsNullOrWhiteSpace(diagnostic), "Invalid XML did not retain its payload and diagnostic.");
        byte[] empty = Array.Empty<byte>();
        Require(ReferenceEquals(empty, ClientSnapshotSaveData.RemovePendingAchievementQueue(empty, out diagnostic)) && diagnostic is null,
            "An empty save was changed by cleanup.");
    }

    private static void CheckFailure(byte[] data, string user, string colony, SnapshotPackageError expected, string expectedCode)
    {
        var result = ClientSnapshotPackageBuilder.FromSaveBytes(data, "failure", user, colony, Timestamp);
        Require(!result.Success && result.ErrorKind == expected && result.ErrorCode == expectedCode && result.Message is null
            && result.Package is null && result.Payload is null, "Known package failure lost its data-only classification: " + expected);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
