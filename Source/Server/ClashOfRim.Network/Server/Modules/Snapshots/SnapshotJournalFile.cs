using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

// Intent checkpoints and confirmation receipts share the same durable file publication.
internal static class SnapshotJournalFile
{
    internal static void Write<T>(string path, T record, bool replace = false)
    {
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: replace);
    }
}
