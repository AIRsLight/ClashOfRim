using System;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Modules.Transport;

internal static class ClientServerTime
{
    internal static bool TryParseUtc(string? value, out DateTimeOffset parsed) => DateTimeOffset.TryParse(value,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);
}
