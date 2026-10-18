using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.RemoteMaps;

internal static class ClientColonySnapshotDownloadTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (string encoding in new[] { "RawRws", "rawrws", "GzipRws", "gZIPRws" })
            foreach (bool existing in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                var f = new Fixture(encoding, existing);
                var saved = Open(await f.Download());
                Check(f.Calls == 1 && f.Progress == 1, "Download did not share the captured transport/progress callback.");
                Check(saved.SnapshotId == "latest-é" && saved.LineageToken == "next-谱系" && saved.SaveName == "ClashOfRim_server_actor_é_home_世界_latest-é", "Restore changed authoritative identity or save name.");
                var xml = XDocument.Parse(Encoding.UTF8.GetString(saved.SaveBytes), LoadOptions.PreserveWhitespace);
                Check(xml.Root!.Element("meta")!.Element("fileName")!.Value == saved.SaveName, "Save name was not written.");
                var components = xml.Root.Element("game")!.Element("components")!;
                var component = ClientSnapshotSaveData.FindComponent(xml)!;
                Check(components.Elements("li").Count(x => x.Attribute("Class")!.Value.Contains("ClashOfRimGameComponent")) == 1
                    && component.Element("clashOfRimLineageSnapshotId")!.Value == saved.SnapshotId
                    && component.Element("clashOfRimLineageToken")!.Value == saved.LineageToken, "Restore duplicated the component or lost authoritative lineage.");
                var original = XDocument.Parse(Encoding.UTF8.GetString(f.Original), LoadOptions.PreserveWhitespace);
                Check(XNode.DeepEquals(original.Root!.Element("game")!.Element("maps"), xml.Root.Element("game")!.Element("maps"))
                    && xml.Root.Element("meta")!.Element("gameVersion")!.Value == "1.7.0", "Restore changed unrelated pawn/item/map data.");
                if (existing) Check(component.Element("custom")!.Value == "keep-é", "Restore replaced existing component contents.");
                Check(!saved.SaveBytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }), "Restore unexpectedly added a UTF-8 BOM.");
                Check(ServerSnapshotRestoreService.TryBuildServerSessionSaveBytes(f.Package, f.Payload, f.Context.UserId, f.Context.ColonyId,
                    out string name, out byte[] bytes, out string error) && name == saved.SaveName && bytes.SequenceEqual(saved.SaveBytes) && error == "", "Public facade diverged from the module.");
                // The prepared result owns its bytes and copied metadata after the transport returns.
                f.Metadata.SnapshotId = "changed"; f.Package.SnapshotId = "changed"; f.Package.NextLineageToken = "changed";
                f.Metadata.ActiveRaidRecovery!.EventId = "changed"; f.Metadata.ActiveRaidRecovery.DeadlineUtc = "changed";
                Array.Fill(f.Payload, (byte)0);
                Check(saved.SnapshotId == "latest-é" && saved.LineageToken == "next-谱系" && saved.ActiveRaidRecovery!.EventId == "raid-é"
                    && saved.ActiveRaidRecovery.DeadlineUtc == "deadline" && saved.ActiveRaidRecovery.DefenderColonyId == "defender-colony"
                    && Encoding.UTF8.GetString(saved.SaveBytes).Contains("keep-é"), "Transport mutation changed the prepared snapshot.");
            }
            await MetadataFailures();
            await SaveFailures();
            await Cancellation();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static async Task MetadataFailures()
    {
        foreach (var entry in new (Action<Fixture> Change, ClientColonySnapshotFailureKind Kind)[]
        {
            (f => f.MetadataResult = ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Failed("network", "offline"), ClientColonySnapshotFailureKind.Metadata),
            (f => f.MetadataResult = ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Ok(null!), ClientColonySnapshotFailureKind.Metadata),
            (f => f.Metadata.Result = new() { Accepted = false, Message = "denied" }, ClientColonySnapshotFailureKind.Rejected),
            (f => f.Metadata.Result = null, ClientColonySnapshotFailureKind.Rejected),
            (f => f.Metadata.Package = null, ClientColonySnapshotFailureKind.MissingSnapshot),
            (f => f.Metadata.SnapshotId = " ", ClientColonySnapshotFailureKind.MissingSnapshot),
            (f => f.PayloadResult = null, ClientColonySnapshotFailureKind.Payload),
            (f => f.PayloadResult = ClashOfRimClientNetworkResult<byte[]>.Ok(null!), ClientColonySnapshotFailureKind.Payload),
            (f => f.PayloadResult = ClashOfRimClientNetworkResult<byte[]>.Failed("payload", "offline"), ClientColonySnapshotFailureKind.Payload),
            (f => f.Package.OwnerId = "ACTOR-é", ClientColonySnapshotFailureKind.Identity),
            (f => f.Package.ColonyId = "other", ClientColonySnapshotFailureKind.Identity),
            (f => f.Package.SnapshotId = "another", ClientColonySnapshotFailureKind.SnapshotId),
            (f => f.Package.SnapshotId = "", ClientColonySnapshotFailureKind.SnapshotId)
        })
        {
            var f = new Fixture(); entry.Change(f);
            var failure = Failed(await f.Download(), entry.Kind);
            if (entry.Kind == ClientColonySnapshotFailureKind.SnapshotId)
                Check(failure.Expected == "latest-é" && failure.Actual == f.Package.SnapshotId, "Lost mismatched ID diagnostics.");
        }
        var fallback = new Fixture(); fallback.Metadata.SnapshotId = null; fallback.Metadata.ActiveRaidRecovery = null;
        Check(Open(await fallback.Download()).SnapshotId == "latest-é" && Open(await fallback.Download()).ActiveRaidRecovery is null, "Package ID fallback or missing recovery changed.");
        // CurrentSnapshotId is deliberately older: server-authoritative latest remains accepted.
        var latest = new Fixture(); Check(Open(await latest.Download()).SnapshotId != latest.Context.CurrentSnapshotId, "Download required the obsolete local base.");
    }

    private static async Task SaveFailures()
    {
        foreach (var entry in new (Action<Fixture> Change, ClientSnapshotRestoreFailureKind Kind)[]
        {
            (f => f.Package.PayloadBytes++, ClientSnapshotRestoreFailureKind.PayloadSizeMismatch),
            (f => f.Package.PayloadSha256 = "", ClientSnapshotRestoreFailureKind.PayloadHashMismatch),
            (f => f.Payload[0] ^= 1, ClientSnapshotRestoreFailureKind.PayloadHashMismatch),
            (f => f.Package.PayloadEncoding = "unknown-é", ClientSnapshotRestoreFailureKind.UnsupportedEncoding),
            (f => { f.Package.PayloadEncoding = "GzipRws"; f.Payload = new byte[] { 0, 1, 2, 3, 4, 5 }; f.SetPayload(); }, ClientSnapshotRestoreFailureKind.DecodeFailed),
            (f => f.Package.OriginalSaveBytes++, ClientSnapshotRestoreFailureKind.OriginalSizeMismatch),
            (f => f.Package.OriginalSha256 = "wrong", ClientSnapshotRestoreFailureKind.OriginalHashMismatch),
            (f => f.SetOriginal("<savegame>"), ClientSnapshotRestoreFailureKind.MetadataRewriteFailed),
            (f => f.SetOriginal("<!DOCTYPE savegame [<!ENTITY x 'no'>]><savegame>&x;</savegame>"), ClientSnapshotRestoreFailureKind.MetadataRewriteFailed)
        })
        {
            var f = new Fixture(); entry.Change(f);
            var failure = Failed(await f.Download(), ClientColonySnapshotFailureKind.Save);
            Check(failure.SaveFailure?.Kind == entry.Kind, "Wrong save failure: " + failure.SaveFailure);
            Check(!ClientSnapshotRestoreSave.TryBuild(f.Package, f.Payload, "u", "c", out _, out _, out var direct) && direct?.Kind == entry.Kind,
                "Direct validation did not share download failures.");
        }
        Check(!ClientSnapshotRestoreSave.TryBuild(null, Array.Empty<byte>(), "u", "c", out _, out _, out var missing)
            && missing?.Kind == ClientSnapshotRestoreFailureKind.PackageMissing, "Null compatibility package was accepted.");
        var optional = new Fixture(); optional.Package.PayloadBytes = optional.Package.OriginalSaveBytes = 0;
        optional.Package.PayloadSha256 = optional.Package.PayloadSha256.ToUpperInvariant(); optional.Package.OriginalSha256 = optional.Package.OriginalSha256.ToUpperInvariant();
        Open(await optional.Download()); // Optional sizes and case-insensitive hashes retain their contract.
        optional = new Fixture(); optional.Package.NextLineageToken = null;
        Check(Open(await optional.Download()).LineageToken == "", "Missing lineage token was not normalized.");
    }

    private static async Task Cancellation()
    {
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var f = new Fixture();
            await MustCancel(f.Download(cancelled.Token)); Check(f.Calls == 0, "Cancelled restore reached the transport.");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var f = new Fixture { DuringDownload = cancelled.Cancel };
            await MustCancel(f.Download(cancelled.Token)); Check(f.Calls == 1, "Cancelled restore retried the transport.");
        }
        var throwing = new Fixture { DuringDownload = () => throw new InvalidOperationException("transport fault") };
        try { await throwing.Download(); throw new Exception("Transport exception was hidden."); }
        catch (InvalidOperationException e) when (e.Message == "transport fault") { }
    }
    private static async Task MustCancel(Task task)
    { try { await task; throw new Exception("Cancellation produced a loadable snapshot."); } catch (OperationCanceledException) { } }
    private static ClientRestoredSnapshot Open(ClientColonySnapshotDownloadResult result)
    { Check(result.Snapshot is not null && result.Failure is null, "Restore failed: " + result.Failure); return result.Snapshot!; }
    private static ClientColonySnapshotFailure Failed(ClientColonySnapshotDownloadResult result, ClientColonySnapshotFailureKind kind)
    { Check(result.Snapshot is null && result.Failure?.Kind == kind, "Unexpected result: " + result.Failure); return result.Failure!; }

    private sealed class Fixture : IClientRemoteSnapshotTransport
    {
        internal readonly ClashOfRimClientNetworkContext Context = new("http://server", "actor/é", "home:世界", "old-base", null, null, "token-é");
        internal readonly ModSnapshotPackageMetadataDto Package = new() { OwnerId = "actor/é", ColonyId = "home:世界", SnapshotId = "latest-é", NextLineageToken = "next-谱系" };
        internal readonly ModDownloadLatestSnapshotResponseDto Metadata;
        internal ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto> MetadataResult;
        internal ClashOfRimClientNetworkResult<byte[]>? PayloadResult;
        internal byte[] Original = Array.Empty<byte>(), Payload = Array.Empty<byte>();
        internal int Calls, Progress;
        internal Action? DuringDownload;
        internal Fixture(string encoding = "RawRws", bool existing = true)
        {
            Package.PayloadEncoding = encoding;
            string component = existing ? "<components><li Class='AIRsLight.ClashOfRim.ClashOfRimGameComponent, ClashOfRim'><custom>keep-é</custom><clashOfRimLineageSnapshotId>old</clashOfRimLineageSnapshotId><clashOfRimLineageToken>old-token</clashOfRimLineageToken></li></components>" : "";
            SetOriginal("<savegame><meta><gameVersion>1.7.0</gameVersion><fileName>old-name</fileName></meta><game>" + component
                + "<maps><li><pawn>keep-é</pawn><health>0.75</health><stuff>黄金</stuff><comp><a>1</a></comp></li></maps></game></savegame>");
            Metadata = new() { Result = new() { Accepted = true }, SnapshotId = Package.SnapshotId, Package = Package,
                ActiveRaidRecovery = new() { EventId = "raid-é", Status = "active", ServerNowUtc = "now", StartedAtUtc = "started", DeadlineUtc = "deadline", FinalDeadlineUtc = "final",
                    DefenderUserId = "defender", DefenderColonyId = "defender-colony", DefenderSnapshotId = "defender-snapshot" } };
            MetadataResult = ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Ok(Metadata);
        }
        internal void SetOriginal(string xml)
        {
            Original = Encoding.UTF8.GetBytes(xml);
            Package.OriginalSaveBytes = Original.Length;
            Package.OriginalSha256 = Convert.ToHexString(SHA256.HashData(Original));
            Payload = Package.PayloadEncoding.Equals("RawRws", StringComparison.OrdinalIgnoreCase) ? Original.ToArray() : ClientSnapshotPayloadCodec.Encode(Original);
            SetPayload();
        }
        internal void SetPayload()
        {
            Package.PayloadBytes = Payload.Length; Package.PayloadSha256 = Convert.ToHexString(SHA256.HashData(Payload));
            PayloadResult = ClashOfRimClientNetworkResult<byte[]>.Ok(Payload);
        }
        internal Task<ClientColonySnapshotDownloadResult> Download(CancellationToken token = default) => new ClientColonySnapshotDownload(this)
            .DownloadAsync(Context, token, id => { Check(id == "latest-é", "Progress used a different snapshot."); Progress++; });
        public Task<RemoteSnapshotDownloadResult> DownloadAsync(ClashOfRimClientNetworkContext context, ClientSnapshotDownloadTarget target,
            CancellationToken token, Action<string>? payloadStarting = null)
        {
            Calls++; Check(ReferenceEquals(context, Context) && target.UserId == Context.UserId && target.ColonyId == Context.ColonyId
                && target.AuthorizationEventId is null && target.AuthorizationScope is null, "Own-colony download changed captured identity or borrowed remote authorization.");
            payloadStarting?.Invoke("latest-é"); DuringDownload?.Invoke();
            return Task.FromResult(new RemoteSnapshotDownloadResult(MetadataResult, PayloadResult));
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
