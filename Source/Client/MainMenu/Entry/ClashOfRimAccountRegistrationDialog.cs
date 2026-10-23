using System;
using AIRsLight.ClashOfRim.Modules.World;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

public sealed class ClashOfRimAccountRegistrationDialog : Window
{
    private const string PasswordInputControl = "ClashOfRim.AccountRegistration.Password";
    private const string PasswordConfirmationInputControl = "ClashOfRim.AccountRegistration.PasswordConfirmation";
    private readonly ClientWorldEntryForm form;

    // Compatibility constructor only; the window never retains the Mod.
    public ClashOfRimAccountRegistrationDialog(ClashOfRimMod mod, string serverBaseUrl, string userId)
        : this((mod ?? throw new ArgumentNullException(nameof(mod))).CreateAccountRegistrationForm(serverBaseUrl, userId)) { }

    internal ClashOfRimAccountRegistrationDialog(ClientWorldEntryForm form)
    {
        this.form = form;
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
    }

    public override Vector2 InitialSize => new(520f, 240f);

    public override void DoWindowContents(Rect inRect)
    {
        if (!form.IsCurrent) { Close(); return; }
        Text.Font = GameFont.Medium;
        Widgets.Label(
            new Rect(inRect.x, inRect.y, inRect.width, 30f),
            ClashOfRimText.Key("ClashOfRim.ServerEntry.CreateAccountTitle"));

        Text.Font = GameFont.Small;
        ClashOfRimUiUtility.DrawTruncatedLabel(
            new Rect(inRect.x, inRect.y + 40f, inRect.width, 28f),
            ClashOfRimText.Key(
                "ClashOfRim.ServerEntry.CreateAccountUser",
                form.UserId.Named("USER")));

        const float labelWidth = 150f;
        Rect passwordLabelRect = new(inRect.x, inRect.y + 78f, labelWidth, 28f);
        Widgets.Label(passwordLabelRect, ClashOfRimText.Key("ClashOfRim.Password"));
        GUI.SetNextControlName(PasswordInputControl);
        form.Password = ClashPasswordField.Draw(
            new Rect(passwordLabelRect.xMax + 8f, passwordLabelRect.y, inRect.width - labelWidth - 8f, 28f),
            form.Password);

        Rect confirmationLabelRect = new(inRect.x, inRect.y + 116f, labelWidth, 28f);
        Widgets.Label(
            confirmationLabelRect,
            ClashOfRimText.Key("ClashOfRim.ServerEntry.PasswordConfirmation"));
        GUI.SetNextControlName(PasswordConfirmationInputControl);
        form.PasswordConfirmation = ClashPasswordField.Draw(
            new Rect(confirmationLabelRect.xMax + 8f, confirmationLabelRect.y, inRect.width - labelWidth - 8f, 28f),
            form.PasswordConfirmation);

        HandleTabFocus();

        if (Widgets.ButtonText(
                new Rect(inRect.xMax - 204f, inRect.yMax - 36f, 96f, 32f),
                ClashOfRimText.Key("ClashOfRim.Cancel")))
        {
            Close();
        }

        if (Widgets.ButtonText(
                new Rect(inRect.xMax - 100f, inRect.yMax - 36f, 100f, 32f),
                ClashOfRimText.Key("ClashOfRim.ServerEntry.CreateAccount")))
        {
            Submit();
        }
    }

    public override void OnAcceptKeyPressed()
    {
        Submit();
        Event.current?.Use();
    }

    public override void PostClose()
    {
        form.Close();
        base.PostClose();
    }

    private void Submit()
    {
        if (form.Submit()) Close();
    }

    private static void HandleTabFocus()
    {
        if (Event.current is not { type: EventType.KeyDown, keyCode: KeyCode.Tab })
        {
            return;
        }

        GUI.FocusControl(GUI.GetNameOfFocusedControl() == PasswordInputControl
            ? PasswordConfirmationInputControl
            : PasswordInputControl);
        Event.current.Use();
    }
}
