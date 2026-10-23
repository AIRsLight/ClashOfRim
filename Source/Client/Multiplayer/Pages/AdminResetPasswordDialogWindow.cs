using AIRsLight.ClashOfRim.Modules.Multiplayer;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class AdminResetPasswordDialogWindow : Window
{
    private readonly ClientAdminPlayerPanel.PasswordDraft draft;

    internal AdminResetPasswordDialogWindow(ClientAdminPlayerPanel.PasswordDraft draft)
    {
        this.draft = draft;
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
    }

    public override void PostClose() { draft.Close(); base.PostClose(); }

    public override Vector2 InitialSize => new(460f, 190f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 30f), ClashOfRimText.Key("ClashOfRim.Admin.ActionResetPassword"));
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inRect.x, inRect.y + 42f, inRect.width, 24f), ClashOfRimText.Key(
            "ClashOfRim.Admin.ResetPasswordTarget",
            draft.Target.UserId.Named("USER")));
        Widgets.Label(new Rect(inRect.x, inRect.y + 76f, 110f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.NewPassword"));
        draft.Value = ClashPasswordField.Draw(new Rect(inRect.x + 120f, inRect.y + 74f, inRect.width - 120f, 28f), draft.Value);

        if (Widgets.ButtonText(new Rect(inRect.xMax - 204f, inRect.yMax - 36f, 96f, 32f), ClashOfRimText.Key("ClashOfRim.Cancel")))
        {
            Close();
        }

        if (Widgets.ButtonText(new Rect(inRect.xMax - 100f, inRect.yMax - 36f, 100f, 32f), ClashOfRimText.Key("ClashOfRim.Confirm"), active: draft.CanSubmit))
        {
            Confirm();
        }
    }

    public override void OnAcceptKeyPressed()
    {
        Confirm();
        Event.current?.Use();
    }

    private void Confirm()
    {
        if (draft.Submit() is not null) Close();
    }
}
