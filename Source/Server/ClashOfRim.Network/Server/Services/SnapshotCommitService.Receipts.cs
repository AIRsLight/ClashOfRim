using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed partial class SnapshotCommitService
{
    private readonly ConcurrentDictionary<string, SnapshotConfirmationReceipt> receipts = new(StringComparer.Ordinal);
    internal SnapshotConfirmationReceipt? FindConfirmationReceipt(SaveSnapshotEnvelope envelope) =>
        FindConfirmationReceipt(envelope.Identity, envelope.OriginalSha256, envelope.PayloadSha256);
    internal SnapshotConfirmationReceipt? FindConfirmationReceipt(SnapshotIdentity identity, string originalHash, string payloadHash) =>
        receipts.GetValueOrDefault(ReceiptKey(identity, originalHash, payloadHash));

    private static string ReceiptKey(SaveSnapshotEnvelope envelope) => ReceiptKey(envelope.Identity, envelope.OriginalSha256, envelope.PayloadSha256);
    private static string ReceiptKey(SnapshotIdentity identity, string originalHash, string payloadHash) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { identity, originalHash, payloadHash })))).ToLowerInvariant();

    private static IEnumerable<SnapshotConfirmationReceipt> ReadReceipts(string root)
    {
        string folder = Path.Combine(root, "confirmation-receipts");
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            var receipt = JsonSerializer.Deserialize<SnapshotConfirmationReceipt>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("Empty confirmation receipt.");
            receipt.Validate();
            if (Path.GetFileNameWithoutExtension(path) != ReceiptKey(receipt.Envelope))
                throw new InvalidDataException("Confirmation receipt filename/identity mismatch.");
            yield return receipt;
        }
    }

    private void StoreReceipt(SnapshotConfirmationReceipt receipt)
    {
        receipt.Validate();
        var key = ReceiptKey(receipt.Envelope);
        if (receipts.TryGetValue(key, out var existing))
        {
            if (JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(receipt))
                throw new InvalidDataException("Snapshot already has a different confirmation receipt.");
            return;
        }
        if (directory is not null)
        {
            string folder = Path.Combine(directory, "confirmation-receipts");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, key + ".json");
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, receipt); stream.Flush(flushToDisk: true); }
            File.Move(path + ".tmp", path);
        }
        receipts[key] = receipt;
    }
}
