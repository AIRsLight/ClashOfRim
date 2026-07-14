using System;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class SessionExpiredWindow : Window
{
    private readonly string title;
    private readonly string message;
    private readonly Action<SessionExpiredWindow> reconnect;
    private readonly Action returnToMainMenu;
    private string status = string.Empty;
    private bool busy;

    public SessionExpiredWindow(
        string title,
        string message,
        Action<SessionExpiredWindow> reconnect,
        Action returnToMainMenu)
    {
        this.title = title ?? string.Empty;
        this.message = message ?? string.Empty;
        this.reconnect = reconnect ?? throw new ArgumentNullException(nameof(reconnect));
        this.returnToMainMenu = returnToMainMenu ?? throw new ArgumentNullException(nameof(returnToMainMenu));
        doCloseX = false;
        closeOnCancel = false;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        preventCameraMotion = true;
        forcePause = true;
    }

    public override Vector2 InitialSize => new(600f, 250f);

    public void CompleteReconnect(bool succeeded, string resultStatus)
    {
        status = resultStatus ?? string.Empty;
        busy = false;
        if (succeeded)
        {
            Close();
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), title);
        Text.Font = GameFont.Small;

        Widgets.Label(new Rect(inRect.x, inRect.y + 48f, inRect.width, 64f), message);
        if (!string.IsNullOrWhiteSpace(status))
        {
            Widgets.Label(new Rect(inRect.x, inRect.y + 116f, inRect.width, 42f), status);
        }

        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && !busy;
        Rect reconnectRect = new(inRect.xMax - 372f, inRect.yMax - 38f, 180f, 32f);
        if (Widgets.ButtonText(reconnectRect, ClashOfRimText.Key("ClashOfRim.SessionExpired.Reconnect")))
        {
            busy = true;
            status = ClashOfRimText.Key("ClashOfRim.SessionExpired.Reconnecting");
            reconnect(this);
        }

        Rect menuRect = new(inRect.xMax - 180f, inRect.yMax - 38f, 180f, 32f);
        if (Widgets.ButtonText(menuRect, ClashOfRimText.Key("ClashOfRim.SessionExpired.ReturnToMainMenu")))
        {
            busy = true;
            returnToMainMenu();
        }

        GUI.enabled = oldEnabled;
    }
}
