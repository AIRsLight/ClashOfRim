using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIRsLight.ClashOfRim.Save;

public static class SaveSnapshotPackageFileWriter
{
    private static readonly byte[] SnapshotPackageMagic = Encoding.ASCII.GetBytes("CORSPKG1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void WriteAtomically(
        string path,
        PersistedSaveSnapshotPackage metadata,
        byte[]? encodedPayload)
    {
        using PreparedFile prepared = Prepare(path, metadata, encodedPayload);
        prepared.Publish();
    }

    // Preparation never changes the destination. The caller may reject a stale
    // candidate and dispose it before preparing another one.
    public static PreparedFile Prepare(
        string path,
        PersistedSaveSnapshotPackage metadata,
        byte[]? encodedPayload,
        PreparedSnapshotPayload? preparedPayload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(metadata);
        if (preparedPayload is not null) preparedPayload.Validate(encodedPayload!);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = File.Create(tempPath))
            {
                // Concatenated gzip members decode to the unchanged CORSPKG1 byte stream.
                // Only the state-dependent metadata prefix is compressed during publication.
                using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
                    Write(gzip, metadata, encodedPayload, writePayload: preparedPayload is null);
                preparedPayload?.WriteTo(stream);
            }

            return new PreparedFile(path, tempPath);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }

    public sealed class PreparedFile : IDisposable
    {
        private readonly string destinationPath;
        private string? pendingPath;

        internal PreparedFile(string destinationPath, string tempPath)
        {
            this.destinationPath = destinationPath;
            pendingPath = tempPath;
        }

        public void Publish()
        {
            if (pendingPath is null) throw new InvalidOperationException("Prepared package has already been consumed.");
            File.Move(pendingPath, destinationPath, overwrite: true);
            pendingPath = null;
        }

        public void Dispose()
        {
            if (pendingPath is null) return;
            File.Delete(pendingPath);
            pendingPath = null;
        }
    }

    private static void Write(
        Stream stream,
        PersistedSaveSnapshotPackage metadata,
        byte[]? encodedPayload,
        bool writePayload = true)
    {
        byte[] metadataJson = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(SnapshotPackageMagic);
        writer.Write((long)metadataJson.LongLength);
        writer.Write(metadataJson);
        writer.Write(encodedPayload?.LongLength ?? -1L);
        if (writePayload && encodedPayload is not null)
        {
            writer.Write(encodedPayload);
        }
    }
}
