using Verse;

namespace AIRsLight.ClashOfRim;

internal static class ClashOfRimText
{
    internal static string Key(string key)
    {
        return key.Translate().ToString();
    }

    internal static string Key(string key, params NamedArgument[] args)
    {
        return key.Translate(args).ToString();
    }

    internal static string SafeArgument(string? value)
    {
        string safe = value ?? string.Empty;
        return safe
            .Replace("{", "｛")
            .Replace("}", "｝");
    }
}
