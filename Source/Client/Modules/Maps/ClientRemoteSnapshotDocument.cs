using System;
using System.IO;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Modules.Maps;

// Owns only serialized data. Reading must not allocate game IDs, translate text,
// invoke compatibility hooks or access Scribe. The game thread consumes it once.
internal sealed class ClientRemoteSnapshotDocument
{
    private XDocument? document;
    internal ClientSnapshotRestoreFailure? Failure { get; }
    private ClientRemoteSnapshotDocument(XDocument? document, ClientSnapshotRestoreFailure? failure)
    { this.document = document; Failure = failure; }

    internal static ClientRemoteSnapshotDocument Read(ModSnapshotPackageMetadataDto package, byte[] payload, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!ClientSnapshotPayloadCodec.TryDecode(package, payload, out var bytes, out var failure))
            return new(null, failure);
        token.ThrowIfCancellationRequested();
        try
        {
            using var source = new MemoryStream(bytes);
            using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            token.ThrowIfCancellationRequested();
            return new(document, null);
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        { return new(null, new(ClientSnapshotRestoreFailureKind.InvalidXml, ex.Message)); }
    }

    internal XDocument Take() => Interlocked.Exchange(ref document, null)
        ?? throw new InvalidOperationException("The prepared remote snapshot document is unavailable or already consumed.");
}
