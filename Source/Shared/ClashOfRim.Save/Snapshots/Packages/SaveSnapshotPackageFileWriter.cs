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
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(metadata);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = File.Create(tempPath))
            using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize))
            {
                Write(gzip, metadata, encodedPayload);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void Write(
        Stream stream,
        PersistedSaveSnapshotPackage metadata,
        byte[]? encodedPayload)
    {
        byte[] metadataJson = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(SnapshotPackageMagic);
        writer.Write((long)metadataJson.LongLength);
        writer.Write(metadataJson);
        writer.Write(encodedPayload?.LongLength ?? -1L);
        if (encodedPayload is not null)
        {
            writer.Write(encodedPayload);
        }
    }
}
