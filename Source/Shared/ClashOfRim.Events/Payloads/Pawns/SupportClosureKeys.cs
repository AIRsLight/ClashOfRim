using System.Text.Json;

namespace AIRsLight.ClashOfRim.Events;

/// <summary>Opaque, versioned keys for support completions. Legacy records retain their original keys.</summary>
public static class SupportClosureKeys
{
    public static string Finish(string sourceEventId, string reason) => Encode("supportfinishv2:", sourceEventId, reason);
    public static string Loss(string sourceEventId, string reason) => Encode("supportlossv2:", sourceEventId, reason);

    public static bool IsFinish(string key, string sourceEventId, string? reason) => !string.IsNullOrWhiteSpace(reason)
        && (key == Finish(sourceEventId, reason!) || key == $"support-finish:{sourceEventId}:{reason}");
    public static bool IsLoss(string key, string sourceEventId, string? reason) => !string.IsNullOrWhiteSpace(reason)
        && (key == Loss(sourceEventId, reason!) || key == $"support-loss:{sourceEventId}:{reason}");

    private static string Encode(string prefix, string sourceEventId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        // Tuple boundaries survive JSON encoding. Hex also survives BuildEventId's punctuation normalization.
        // The alphanumeric namespace cannot normalize to the legacy support-finish/support-loss namespaces.
        return prefix + Convert.ToHexString(JsonSerializer.SerializeToUtf8Bytes(new[] { sourceEventId, reason }));
    }
}
