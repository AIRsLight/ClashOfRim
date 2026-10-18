using System;
using AIRsLight.ClashOfRim.Modules.World;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class SessionExpiredWindow : Window, IClientSessionRecoveryDialog
{
    private readonly string title;
    private readonly string message;
    private readonly Action<SessionExpiredWindow> reconnect;
    private readonly Action<IClientSessionRecoveryDialog> returnToMainMenu;
    private readonly Func<bool> current;
    private string status = string.Empty;
    private bool busy;

    public SessionExpiredWindow(
        string title,
        string message,
        Func<bool> current,
        Action<SessionExpiredWindow> reconnect,
        Action<IClientSessionRecoveryDialog> returnToMainMenu)
    {
        this.title = title ?? string.Empty;
        this.message = message ?? string.Empty;
        this.current = current;
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
    bool IClientSessionRecoveryDialog.IsOpen => ReferenceEquals(Find.WindowStack.WindowOfType<SessionExpiredWindow>(), this);
    void IClientSessionRecoveryDialog.Close() => Close();

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
        if (!current()) { Close(); return; }
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), title);
        Text.Font = GameFont.Small;

        Widgets.Label(new Rect(inRect.x, inRect.y + 48f, inRect.width, 64f), message);
        if (!string.IsNullOrWhiteSpace(status))
        {
            Widgets.Label(new Rect(inRect.x, inRect.y + 116f, inRect.width, 42f), status);
        }

        bool oldEnabled = GUI.enabled;
        try
        {
            GUI.enabled = oldEnabled && !busy;
            Rect reconnectRect = new(inRect.xMax - 372f, inRect.yMax - 38f, 180f, 32f);
            if (Widgets.ButtonText(reconnectRect, ClashOfRimText.Key("ClashOfRim.SessionExpired.Reconnect")))
            {
                busy = true;
                status = ClashOfRimText.Key("ClashOfRim.SessionExpired.Reconnecting");
                reconnect(this);
            }

            GUI.enabled = oldEnabled && !busy;
            Rect menuRect = new(inRect.xMax - 180f, inRect.yMax - 38f, 180f, 32f);
            if (Widgets.ButtonText(menuRect, ClashOfRimText.Key("ClashOfRim.SessionExpired.ReturnToMainMenu")))
            {
                busy = true;
                returnToMainMenu(this);
            }
        }
        finally { GUI.enabled = oldEnabled; }
    }
}
