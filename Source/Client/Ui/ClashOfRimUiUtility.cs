using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim;

internal static class ClashOfRimUiUtility
{
    private static readonly Color DangerBackground = new(0.48f, 0.12f, 0.12f);
    private static readonly Color DangerBorder = new(0.88f, 0.32f, 0.28f);

    public static float ButtonWidth(string label, float minimum = 54f) =>
        Mathf.Max(minimum, Text.CalcSize(label).x + 16f);

    public static float WrappedLabel(Rect area, string label, float minimumHeight = 24f)
    {
        float height = Mathf.Max(minimumHeight, Text.CalcHeight(label, area.width));
        Widgets.Label(new Rect(area.x, area.y, area.width, height), label);
        return height;
    }

    public static void DrawTruncatedLabel(Rect rect, string? label, TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        string text = label ?? string.Empty;
        TextAnchor previousAnchor = Text.Anchor;
        bool previousWordWrap = Text.WordWrap;
        Text.Anchor = anchor;
        Text.WordWrap = false;
        string visibleText = text.Replace('\r', ' ').Replace('\n', ' ').Truncate(rect.width, null);
        Widgets.Label(rect, visibleText);
        Text.WordWrap = previousWordWrap;
        Text.Anchor = previousAnchor;

        if (!string.Equals(visibleText, text, System.StringComparison.Ordinal))
        {
            TooltipHandler.TipRegion(rect, text);
        }
    }

    public static string SelectionLabel(string? label)
    {
        string normalized = label?.Trim() ?? string.Empty;
        return normalized.EndsWith("...", System.StringComparison.Ordinal)
            ? normalized
            : string.IsNullOrEmpty(normalized) ? "..." : normalized + " ...";
    }

    public static bool SelectionButton(Rect rect, string label, bool active = true, string? tooltip = null)
    {
        bool pressed = Widgets.ButtonText(rect, SelectionLabel(label), active: active);
        TooltipHandler.TipRegion(
            rect,
            string.IsNullOrWhiteSpace(tooltip)
                ? ClashOfRimText.Key("ClashOfRim.SelectionButtonTip")
                : tooltip);
        return pressed;
    }

    public static bool DangerButton(Rect rect, string label, string tooltip, bool active = true)
    {
        bool pressed = Widgets.CustomButtonText(
            ref rect,
            label,
            DangerBackground,
            Color.white,
            DangerBorder,
            active: active);
        TooltipHandler.TipRegion(rect, tooltip);
        return pressed;
    }
}
