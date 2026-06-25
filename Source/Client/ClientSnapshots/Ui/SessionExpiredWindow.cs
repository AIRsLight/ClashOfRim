using System;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class SessionExpiredWindow : Window
{
    private readonly string message;
    private readonly Action returnToMainMenu;
    private bool returning;

    public SessionExpiredWindow(string message, Action returnToMainMenu)
    {
        this.message = message ?? string.Empty;
        this.returnToMainMenu = returnToMainMenu ?? throw new ArgumentNullException(nameof(returnToMainMenu));
        doCloseX = false;
        closeOnCancel = false;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        preventCameraMotion = true;
        forcePause = true;
    }

    public override Vector2 InitialSize => new(560f, 220f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), ClashOfRimText.Key("ClashOfRim.SessionExpired.Title"));
        Text.Font = GameFont.Small;

        Widgets.Label(new Rect(inRect.x, inRect.y + 48f, inRect.width, 78f), message);

        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && !returning;
        Rect buttonRect = new(inRect.xMax - 180f, inRect.yMax - 38f, 180f, 32f);
        if (Widgets.ButtonText(buttonRect, ClashOfRimText.Key("ClashOfRim.SessionExpired.ReturnToMainMenu")))
        {
            returning = true;
            returnToMainMenu();
        }

        GUI.enabled = oldEnabled;
    }
}
