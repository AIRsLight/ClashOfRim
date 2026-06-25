using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ColonyRelocationConfirmationWindow : Window
{
    private readonly System.Action onConfirm;
    private readonly string colonyLabel;
    private readonly int targetTile;

    public ColonyRelocationConfirmationWindow(string colonyLabel, int targetTile, System.Action onConfirm)
    {
        this.colonyLabel = colonyLabel;
        this.targetTile = targetTile;
        this.onConfirm = onConfirm;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnAccept = false;
        closeOnCancel = true;
        doCloseX = true;
    }

    public override Vector2 InitialSize => new(640f, 360f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), ClashOfRimText.Key("ClashOfRim.ColonyRelocation.Title"));
        Text.Font = GameFont.Small;

        Rect textRect = new(inRect.x, inRect.y + 48f, inRect.width, inRect.height - 116f);
        Widgets.Label(
            textRect,
            ClashOfRimText.Key(
                "ClashOfRim.ColonyRelocation.Warning",
                colonyLabel.Named("COLONY"),
                targetTile.ToString().Named("TILE")));

        Rect cancelRect = new(inRect.xMax - 260f, inRect.yMax - 38f, 120f, 38f);
        if (Widgets.ButtonText(cancelRect, "Cancel".Translate()))
        {
            Close();
        }

        Rect confirmRect = new(inRect.xMax - 130f, inRect.yMax - 38f, 130f, 38f);
        if (Widgets.ButtonText(confirmRect, ClashOfRimText.Key("ClashOfRim.ColonyRelocation.Confirm")))
        {
            Close();
            onConfirm?.Invoke();
        }
    }
}
