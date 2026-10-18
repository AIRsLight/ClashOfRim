using System;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class AbandonPlayerColonyConfirmationWindow : Window
{
    private const float CountdownSeconds = 10f;
    private readonly string colony;
    private readonly Func<Task> confirm;
    private readonly float openedAtRealtime;

    public AbandonPlayerColonyConfirmationWindow(string colony, Func<Task> confirm)
    {
        this.colony = colony;
        this.confirm = confirm;
        openedAtRealtime = Time.realtimeSinceStartup;
        doCloseX = true;
        absorbInputAroundWindow = true;
        forcePause = true;
        closeOnCancel = true;
    }

    public override Vector2 InitialSize => new(560f, 260f);

    public override void DoWindowContents(Rect inRect)
    {
        float remaining = Mathf.Max(0f, CountdownSeconds - (Time.realtimeSinceStartup - openedAtRealtime));
        bool canConfirm = remaining <= 0.01f;

        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.AbandonColony.Title"));
        Text.Font = GameFont.Small;

        Rect messageRect = new(inRect.x, inRect.y + 46f, inRect.width, 112f);
        Widgets.Label(
            messageRect,
            ClashOfRimText.Key(
                "ClashOfRim.AbandonColony.Warning",
                colony.Named("COLONY")));

        Rect countdownRect = new(inRect.x, inRect.y + 160f, inRect.width, 28f);
        string countdownText = canConfirm
            ? ClashOfRimText.Key("ClashOfRim.AbandonColony.CountdownReady")
            : ClashOfRimText.Key(
                "ClashOfRim.AbandonColony.Countdown",
                Mathf.CeilToInt(remaining).Named("SECONDS"));
        Widgets.Label(countdownRect, countdownText);

        Rect cancelRect = new(inRect.xMax - 240f, inRect.yMax - 38f, 110f, 32f);
        if (Widgets.ButtonText(cancelRect, "Cancel".Translate()))
        {
            Close();
        }

        Rect confirmRect = new(inRect.xMax - 120f, inRect.yMax - 38f, 120f, 32f);
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && canConfirm;
        if (Widgets.ButtonText(confirmRect, ClashOfRimText.Key("ClashOfRim.AbandonColony.Confirm")))
        {
            Close();
            _ = confirm();
        }

        GUI.enabled = oldEnabled;
    }
}
