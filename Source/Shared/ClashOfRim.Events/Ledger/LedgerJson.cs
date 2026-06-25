using System.Text.Json;

namespace AIRsLight.ClashOfRim.Events;

internal static class LedgerJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true
    };
}
