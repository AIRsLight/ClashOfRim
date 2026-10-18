using System;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Achievements;

internal static class AchievementText
{
    internal static string Label(string? labelKey, string? achievementId)
    {
        return TryTranslate(labelKey, out string translated) ? translated : string.IsNullOrWhiteSpace(achievementId)
            ? ClashOfRimText.Key("ClashOfRim.Achievement.Unknown") : achievementId!;
    }

    internal static string Description(string? key) => TryTranslate(key, out string translated) ? translated : string.Empty;

    private static bool TryTranslate(string? key, out string translated)
    {
        translated = string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return false;
        translated = ClashOfRimText.Key(key!);
        return !string.Equals(translated, key, StringComparison.Ordinal);
    }

    internal static string Value(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
