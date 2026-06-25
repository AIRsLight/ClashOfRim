using System;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class SnapshotConfirmationFailureWindow : Window
{
    private readonly string operation;
    private readonly string message;
    private readonly Action? retryUpload;
    private readonly Action returnToMainMenu;
    private bool operationInProgress;

    public SnapshotConfirmationFailureWindow(
        string operation,
        string message,
        Action? retryUpload,
        Action returnToMainMenu)
    {
        this.operation = operation ?? string.Empty;
        this.message = message ?? string.Empty;
        this.retryUpload = retryUpload;
        this.returnToMainMenu = returnToMainMenu ?? throw new ArgumentNullException(nameof(returnToMainMenu));
        doCloseX = false;
        absorbInputAroundWindow = true;
        forcePause = true;
        closeOnCancel = false;
        closeOnClickedOutside = false;
    }

    public override Vector2 InitialSize => new(640f, 300f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.Title"));
        Text.Font = GameFont.Small;

        Rect bodyRect = new(inRect.x, inRect.y + 48f, inRect.width, 130f);
        string bodyText = Prefs.DevMode
            ? ClashOfRimText.Key(
                "ClashOfRim.SnapshotConfirmationFailure.Message",
                operation.Named("OPERATION"),
                message.Named("MESSAGE"))
            : ClashOfRimText.Key(
                "ClashOfRim.SnapshotConfirmationFailure.MessageCompact",
                operation.Named("OPERATION"));
        Widgets.Label(
            bodyRect,
            bodyText);

        if (operationInProgress)
        {
            Widgets.Label(
                new Rect(inRect.x, inRect.yMax - 78f, inRect.width, 28f),
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.Waiting"));
        }

        float buttonWidth = 180f;
        Rect discardRect = new(inRect.xMax - buttonWidth, inRect.yMax - 38f, buttonWidth, 32f);
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && !operationInProgress;
        if (Widgets.ButtonText(discardRect, ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.ReturnToMainMenu")))
        {
            operationInProgress = true;
            returnToMainMenu();
        }

        if (retryUpload is not null)
        {
            Rect retryRect = new(discardRect.x - buttonWidth - 12f, inRect.yMax - 38f, buttonWidth, 32f);
            if (Widgets.ButtonText(retryRect, ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.RetryUpload")))
            {
                operationInProgress = true;
                retryUpload();
            }
        }

        GUI.enabled = oldEnabled;
    }
}
