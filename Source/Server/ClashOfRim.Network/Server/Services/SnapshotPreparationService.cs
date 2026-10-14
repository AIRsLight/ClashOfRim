using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

internal static class SnapshotPreparationService
{
    // Limit simultaneous decompression/XML trees without serializing every colony on the mutation gate.
    private static readonly SemaphoreSlim Slots = new(Math.Clamp(Environment.ProcessorCount, 1, 4));

    public static SnapshotUploadResult Prepare(SnapshotUploadReceiver receiver, SnapshotUploadContext context,
        SaveSnapshotPackage package, DateTimeOffset acceptedAtUtc)
    {
        Slots.Wait();
        try { return receiver.Prepare(context, package, acceptedAtUtc); }
        finally { Slots.Release(); }
    }
}
