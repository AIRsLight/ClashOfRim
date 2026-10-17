using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

internal static class ServerText
{
    internal static string Format(string key, params (string Key, string? Value)[] arguments) =>
        ServerLocalization.Text(key, arguments.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
}
