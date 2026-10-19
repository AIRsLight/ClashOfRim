using System;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal static class MultiplayerAdminLayout
{
    internal static void DrawAdminSaveFooter(Rect rect, ClientAdminConfigurationEditor model, ClientAdminConfigurationDraft draft)
    {
        Rect footer = AdminSaveFooterRect(rect);
        bool parsed = draft.IsValid;
        if (!parsed)
        {
            Widgets.Label(new Rect(footer.x, footer.y + 6f, footer.width - 170f, 30f), ClashOfRimText.Key("ClashOfRim.Admin.InvalidConfig"));
        }

        if (Widgets.ButtonText(new Rect(footer.xMax - 150f, footer.y + 2f, 140f, 30f), ClashOfRimText.Key("ClashOfRim.Admin.Save"), active: parsed && !model.Busy))
        {
            _ = model.SaveAsync(draft);
        }
    }

    internal static Rect BeginAdminScroll(Rect rect, ref Vector2 scroll, float minHeight)
    {
        Rect view = new(0f, 0f, rect.width - 16f, Math.Max(rect.height, minHeight));
        Widgets.BeginScrollView(rect, ref scroll, view);
        return view;
    }

    internal static Rect BeginAdminScrollWithSaveFooter(Rect rect, ref Vector2 scroll, float minHeight)
    {
        return BeginAdminScroll(AdminScrollRectWithSaveFooter(rect), ref scroll, minHeight);
    }

    internal static Rect AdminScrollRectWithSaveFooter(Rect rect)
    {
        const float footerHeight = 44f;
        return new Rect(rect.x, rect.y, rect.width, Math.Max(0f, rect.height - footerHeight));
    }

    internal static Rect AdminSaveFooterRect(Rect rect)
    {
        const float footerHeight = 44f;
        return new Rect(rect.x, rect.yMax - footerHeight + 6f, rect.width, footerHeight - 6f);
    }

    internal static void DrawAdminSectionTitle(Rect view, ref float y, string title)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, y, view.width, 28f), title);
        Text.Font = GameFont.Small;
        y += 34f;
    }

    internal static void DrawAdminTextRow(Rect view, ref float y, string label, ref string value)
    {
        Widgets.Label(new Rect(0f, y, 300f, 24f), label);
        value = Widgets.TextField(new Rect(310f, y, 180f, 26f), value);
        y += 32f;
    }

    internal static void DrawAdminCheckboxRow(Rect view, ref float y, string label, ref bool value)
    {
        Widgets.CheckboxLabeled(new Rect(0f, y, Math.Min(view.width, 490f), 28f), label, ref value);
        y += 32f;
    }

    internal static bool DrawAdminSelectionButton(Rect rect, string label, bool active = true)
    {
        return ClashOfRimUiUtility.SelectionButton(rect, label, active);
    }
}
