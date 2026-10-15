using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

internal static class SnapshotPreparationService
{
    // Limit simultaneous decompression, XML trees and payload compression outside the mutation gate.
    private static readonly SemaphoreSlim Slots = new(Math.Clamp(Environment.ProcessorCount, 1, 4));

    public static SnapshotUploadResult Prepare(SnapshotUploadReceiver receiver, SnapshotUploadContext context,
        SaveSnapshotPackage package, DateTimeOffset acceptedAtUtc)
        => Run(() => receiver.Prepare(context, package, acceptedAtUtc));

    public static T Run<T>(Func<T> prepare)
    {
        Slots.Wait();
        try { return prepare(); }
        finally { Slots.Release(); }
    }
}
