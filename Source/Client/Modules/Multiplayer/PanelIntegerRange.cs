using System;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal readonly record struct PanelIntegerRange(int Min, int Max)
{
    internal static PanelIntegerRange Normalize(int min, int max, int fallbackMin = 1, int fallbackMax = 1)
    {
        int normalizedMin = min > 0 ? min : Math.Max(1, fallbackMin);
        return new(normalizedMin, max >= normalizedMin ? max : Math.Max(normalizedMin, fallbackMax));
    }
    // Invalid drafts remain visible in the text field; only the slider uses a fallback.
    internal int SliderValue(string text) => int.TryParse(text, out int value) ? Math.Clamp(value, Min, Max) : Min;
}
