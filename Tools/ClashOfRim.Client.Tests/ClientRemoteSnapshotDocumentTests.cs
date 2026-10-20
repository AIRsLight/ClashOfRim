using System;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Snapshots;

internal static class ClientRemoteSnapshotDocumentTests
{
    internal static void Run()
    {
        const string xml = "<savegame>\r\n <game><maps><li><text>中文 日本語 &amp; é\r\nnext</text><blank>  </blank><data><![CDATA[a<b]]></data></li></maps></game>\r\n</savegame>";
        byte[] original = Encoding.UTF8.GetBytes(xml);
        foreach (bool gzip in new[] { false, true })
        {
            byte[] payload = gzip ? ClientSnapshotPayloadCodec.Encode(original) : original;
            ModSnapshotPackageMetadataDto Package() => new()
            {
                PayloadEncoding = gzip ? "GzipRws" : "RawRws", PayloadBytes = payload.Length,
                PayloadSha256 = ClientSnapshotPayloadCodec.Hash(payload),
                OriginalSaveBytes = original.Length, OriginalSha256 = ClientSnapshotPayloadCodec.Hash(original)
            };
            var prepared = ClientRemoteSnapshotDocument.Read(Package(), payload);
            Check(prepared.Failure is null, "Valid snapshot rejected.");
            // The historical XML parser also normalizes XML newlines while preserving
            // formatting/blank text nodes. Moving threads must not change that tree.
            Check(XNode.DeepEquals(prepared.Take(), XDocument.Parse(xml, LoadOptions.PreserveWhitespace)), "Prepared XML changed save text or whitespace.");
            Reject<InvalidOperationException>(() => prepared.Take());
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Reject<OperationCanceledException>(() => ClientRemoteSnapshotDocument.Read(Package(), payload, cancel.Token));
            foreach (var (kind, change) in new (ClientSnapshotRestoreFailureKind, Action<ModSnapshotPackageMetadataDto>)[]
            {
                (ClientSnapshotRestoreFailureKind.PayloadSizeMismatch, p => p.PayloadBytes++),
                (ClientSnapshotRestoreFailureKind.PayloadHashMismatch, p => p.PayloadSha256 = "bad"),
                (ClientSnapshotRestoreFailureKind.OriginalSizeMismatch, p => p.OriginalSaveBytes++),
                (ClientSnapshotRestoreFailureKind.OriginalHashMismatch, p => p.OriginalSha256 = "bad"),
                (ClientSnapshotRestoreFailureKind.UnsupportedEncoding, p => p.PayloadEncoding = "other")
            })
            {
                var package = Package(); change(package);
                Check(ClientRemoteSnapshotDocument.Read(package, payload).Failure?.Kind == kind, "Validation failed with wrong category: " + kind);
                Check(!ClientSnapshotRestoreSave.TryBuild(package, payload, "u", "c", out _, out _, out var failure)
                    && failure?.Kind == kind, "Remote and full-save validation diverged: " + kind);
            }
            var caseInsensitive = Package();
            caseInsensitive.PayloadSha256 = caseInsensitive.PayloadSha256.ToUpperInvariant();
            caseInsensitive.OriginalSha256 = caseInsensitive.OriginalSha256.ToUpperInvariant();
            Check(ClientRemoteSnapshotDocument.Read(caseInsensitive, payload).Failure is null, "Hash comparison changed case policy.");
        }
        foreach (string invalid in new[] { "<savegame>", "<!DOCTYPE savegame [<!ENTITY x 'expanded'>]><savegame>&x;</savegame>" })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(invalid);
            var package = new ModSnapshotPackageMetadataDto { PayloadEncoding = "RawRws", PayloadSha256 = ClientSnapshotPayloadCodec.Hash(bytes),
                OriginalSha256 = ClientSnapshotPayloadCodec.Hash(bytes) };
            Check(ClientRemoteSnapshotDocument.Read(package, bytes).Failure?.Kind == ClientSnapshotRestoreFailureKind.InvalidXml,
                "Malformed XML/DTD was admitted.");
        }
        byte[] corrupt = { 1, 2, 3, 4 };
        Check(ClientRemoteSnapshotDocument.Read(new() { PayloadEncoding = "GzipRws", PayloadSha256 = ClientSnapshotPayloadCodec.Hash(corrupt) }, corrupt)
            .Failure?.Kind == ClientSnapshotRestoreFailureKind.DecodeFailed, "Corrupt gzip was admitted.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
