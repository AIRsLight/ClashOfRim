using System;
using System.Collections.Generic;
using System.Globalization;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ColonyAppearanceDialogWindow : Window
{
    private const float SwatchSize = 26f;
    private const float IconSize = 46f;
    private readonly string colony;
    private readonly Action<ColonyAppearanceSelection> save;
    private readonly bool ideologyAvailable;
    private readonly List<ColorChoice> fixedColors;
    private Vector2 iconScroll;
    private IdeoIconDef? selectedIconDef;
    private ColorDef? selectedColorDef;
    private string selectedHex;

    public ColonyAppearanceDialogWindow(string colony, ColonyAppearanceSelection current, Action<ColonyAppearanceSelection> save)
    {
        this.save = save;
        this.colony = colony;
        doCloseX = true;
        closeOnCancel = true;
        absorbInputAroundWindow = true;
        forcePause = false;

        fixedColors = BuildFixedColors();
        ideologyAvailable = ModsConfig.IdeologyActive
            && DefDatabase<IdeoIconDef>.AllDefsListForReading.Count > 0
            && IdeoColors.Count > 0;
        if (ideologyAvailable)
        {
            selectedIconDef = (!string.IsNullOrWhiteSpace(current.IconDefName)
                    ? DefDatabase<IdeoIconDef>.GetNamedSilentFail(current.IconDefName)
                    : null)
                ?? DefDatabase<IdeoIconDef>.AllDefsListForReading[0];
            selectedColorDef = (!string.IsNullOrWhiteSpace(current.ColorDefName)
                    ? DefDatabase<ColorDef>.GetNamedSilentFail(current.ColorDefName)
                    : null)
                ?? IdeoColors[0];
            selectedHex = ToHtml(selectedColorDef.color);
        }
        else
        {
            selectedHex = string.IsNullOrWhiteSpace(current.ColorHex)
                ? fixedColors[0].Hex
                : current.ColorHex;
        }
    }

    public override Vector2 InitialSize => new(680f, ideologyAvailable ? 620f : 360f);

    private static List<ColorDef> IdeoColors
    {
        get
        {
            List<ColorDef> colors = DefDatabase<ColorDef>.AllDefsListForReading
                .FindAll(color => color.colorType == ColorType.Ideo);
            colors.SortByColor(color => color.color);
            return colors;
        }
    }

    public override void OnAcceptKeyPressed()
    {
        Save();
        Event.current.Use();
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.ColonyAppearance.Title"));
        Text.Font = GameFont.Small;

        string colonyLabel = colony;
        Widgets.Label(
            new Rect(inRect.x, inRect.y + 40f, inRect.width, 24f),
            ClashOfRimText.Key("ClashOfRim.ColonyAppearance.Target", colonyLabel.Named("COLONY")));

        Rect previewRect = new(inRect.x, inRect.y + 76f, 96f, 96f);
        DrawPreview(previewRect);

        Rect modeRect = new(previewRect.xMax + 18f, previewRect.y, inRect.width - previewRect.width - 18f, 96f);
        string modeText = ideologyAvailable
            ? ClashOfRimText.Key("ClashOfRim.ColonyAppearance.IdeologyMode")
            : ClashOfRimText.Key("ClashOfRim.ColonyAppearance.ColorOnlyMode");
        Widgets.Label(modeRect, modeText);

        float contentY = previewRect.yMax + 18f;
        if (ideologyAvailable)
        {
            contentY = DrawIdeologySelectors(new Rect(inRect.x, contentY, inRect.width, inRect.yMax - contentY - 48f));
        }
        else
        {
            DrawFixedColorSelector(new Rect(inRect.x, contentY, inRect.width, 80f));
        }

        Rect resetRect = new(inRect.x, inRect.yMax - 38f, 110f, 32f);
        if (Widgets.ButtonText(resetRect, ClashOfRimText.Key("ClashOfRim.ColonyAppearance.Reset")))
        {
            save(ColonyAppearanceSelection.Empty);
            Close();
        }

        Rect cancelRect = new(inRect.xMax - 226f, inRect.yMax - 38f, 100f, 32f);
        if (Widgets.ButtonText(cancelRect, ClashOfRimText.Key("ClashOfRim.Cancel")))
        {
            Close();
        }

        Rect saveRect = new(inRect.xMax - 116f, inRect.yMax - 38f, 116f, 32f);
        if (Widgets.ButtonText(saveRect, ClashOfRimText.Key("ClashOfRim.Save")))
        {
            Save();
        }
    }

    private float DrawIdeologySelectors(Rect rect)
    {
        float curY = rect.y;
        Widgets.Label(new Rect(rect.x, curY, rect.width, 24f), ClashOfRimText.Key("ClashOfRim.ColonyAppearance.Color"));
        curY += 28f;

        float x = rect.x;
        foreach (ColorDef colorDef in IdeoColors)
        {
            Rect swatch = new(x, curY, SwatchSize, SwatchSize);
            Widgets.DrawBoxSolid(swatch.ContractedBy(2f), colorDef.color);
            Widgets.DrawHighlightIfMouseover(swatch);
            if (selectedColorDef == colorDef)
            {
                Widgets.DrawBox(swatch, 2);
            }

            if (Widgets.ButtonInvisible(swatch))
            {
                selectedColorDef = colorDef;
                selectedHex = ToHtml(colorDef.color);
            }

            x += SwatchSize + 4f;
            if (x + SwatchSize > rect.xMax)
            {
                x = rect.x;
                curY += SwatchSize + 4f;
            }
        }

        curY += SwatchSize + 12f;
        Widgets.Label(new Rect(rect.x, curY, rect.width, 24f), ClashOfRimText.Key("ClashOfRim.ColonyAppearance.Icon"));
        curY += 28f;

        Rect scrollRect = new(rect.x, curY, rect.width, rect.yMax - curY);
        float viewHeight = Mathf.CeilToInt(DefDatabase<IdeoIconDef>.AllDefsListForReading.Count / 10f) * (IconSize + 6f) + 12f;
        Rect viewRect = new(0f, 0f, scrollRect.width - 16f, Mathf.Max(scrollRect.height, viewHeight));
        Widgets.BeginScrollView(scrollRect, ref iconScroll, viewRect);
        int columns = Mathf.Max(1, Mathf.FloorToInt(viewRect.width / (IconSize + 6f)));
        int index = 0;
        foreach (IdeoIconDef iconDef in DefDatabase<IdeoIconDef>.AllDefsListForReading)
        {
            int row = index / columns;
            int column = index % columns;
            Rect iconRect = new(
                column * (IconSize + 6f),
                row * (IconSize + 6f),
                IconSize,
                IconSize);
            Widgets.DrawLightHighlight(iconRect);
            Widgets.DrawHighlightIfMouseover(iconRect);
            if (selectedIconDef == iconDef)
            {
                Widgets.DrawBox(iconRect, 2);
            }

            GUI.color = selectedColorDef?.color ?? Color.white;
            GUI.DrawTexture(iconRect.ContractedBy(5f), iconDef.Icon);
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(iconRect))
            {
                selectedIconDef = iconDef;
            }

            index++;
        }

        Widgets.EndScrollView();
        return scrollRect.yMax;
    }

    private void DrawFixedColorSelector(Rect rect)
    {
        Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f), ClashOfRimText.Key("ClashOfRim.ColonyAppearance.Color"));
        float x = rect.x;
        float y = rect.y + 30f;
        foreach (ColorChoice choice in fixedColors)
        {
            Rect swatch = new(x, y, SwatchSize, SwatchSize);
            Widgets.DrawBoxSolid(swatch.ContractedBy(2f), choice.Color);
            Widgets.DrawHighlightIfMouseover(swatch);
            if (string.Equals(selectedHex, choice.Hex, StringComparison.OrdinalIgnoreCase))
            {
                Widgets.DrawBox(swatch, 2);
            }

            if (Widgets.ButtonInvisible(swatch))
            {
                selectedHex = choice.Hex;
            }

            x += SwatchSize + 6f;
        }
    }

    private void DrawPreview(Rect rect)
    {
        Widgets.DrawLightHighlight(rect);
        Texture2D icon = ideologyAvailable && selectedIconDef?.Icon != null
            ? selectedIconDef.Icon
            : ContentFinder<Texture2D>.Get("World/WorldObjects/Expanding/Town", reportFailure: false) ?? BaseContent.BadTex;
        GUI.color = ideologyAvailable
            ? selectedColorDef?.color ?? Color.white
            : ParseHtmlColor(selectedHex);
        GUI.DrawTexture(rect.ContractedBy(14f), icon);
        GUI.color = Color.white;
    }

    private void Save()
    {
        if (ideologyAvailable)
        {
            save(new ColonyAppearanceSelection(
                "Ideology",
                selectedIconDef?.defName,
                selectedColorDef?.defName,
                selectedColorDef is null ? selectedHex : ToHtml(selectedColorDef.color)));
        }
        else
        {
            save(new ColonyAppearanceSelection("Color", null, null, selectedHex));
        }

        Close();
    }

    private static List<ColorChoice> BuildFixedColors()
    {
        return new List<ColorChoice>
        {
            new("#c7b48a"),
            new("#7fa6c7"),
            new("#7eaf78"),
            new("#c36b58"),
            new("#a88ac6"),
            new("#c89b4d"),
            new("#a8aeb3"),
            new("#d19aa4")
        };
    }

    private static string ToHtml(Color color)
    {
        Color32 color32 = color;
        return "#"
            + color32.r.ToString("x2", CultureInfo.InvariantCulture)
            + color32.g.ToString("x2", CultureInfo.InvariantCulture)
            + color32.b.ToString("x2", CultureInfo.InvariantCulture);
    }

    private static Color ParseHtmlColor(string? hex)
    {
        return !string.IsNullOrWhiteSpace(hex) && ColorUtility.TryParseHtmlString(hex, out Color color)
            ? color
            : Color.white;
    }

    private readonly struct ColorChoice
    {
        public ColorChoice(string hex)
        {
            Hex = hex;
            Color = ParseHtmlColor(hex);
        }

        public string Hex { get; }

        public Color Color { get; }
    }
}
