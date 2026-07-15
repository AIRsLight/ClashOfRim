using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

public sealed class ClashOfRimAccountRegistrationDialog : Window
{
    private const string PasswordInputControl = "ClashOfRim.AccountRegistration.Password";
    private const string PasswordConfirmationInputControl = "ClashOfRim.AccountRegistration.PasswordConfirmation";
    private readonly ClashOfRimMod mod;
    private readonly string serverBaseUrl;
    private readonly string userId;
    private string password = string.Empty;
    private string passwordConfirmation = string.Empty;

    public ClashOfRimAccountRegistrationDialog(
        ClashOfRimMod mod,
        string serverBaseUrl,
        string userId)
    {
        this.mod = mod;
        this.serverBaseUrl = serverBaseUrl;
        this.userId = userId;
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
    }

    public override Vector2 InitialSize => new(520f, 240f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(
            new Rect(inRect.x, inRect.y, inRect.width, 30f),
            ClashOfRimText.Key("ClashOfRim.ServerEntry.CreateAccountTitle"));

        Text.Font = GameFont.Small;
        Widgets.Label(
            new Rect(inRect.x, inRect.y + 40f, inRect.width, 28f),
            ClashOfRimText.Key(
                "ClashOfRim.ServerEntry.CreateAccountUser",
                userId.Named("USER")));

        const float labelWidth = 150f;
        Rect passwordLabelRect = new(inRect.x, inRect.y + 78f, labelWidth, 28f);
        Widgets.Label(passwordLabelRect, ClashOfRimText.Key("ClashOfRim.Password"));
        GUI.SetNextControlName(PasswordInputControl);
        password = GUI.PasswordField(
            new Rect(passwordLabelRect.xMax + 8f, passwordLabelRect.y, inRect.width - labelWidth - 8f, 28f),
            password ?? string.Empty,
            '*');

        Rect confirmationLabelRect = new(inRect.x, inRect.y + 116f, labelWidth, 28f);
        Widgets.Label(
            confirmationLabelRect,
            ClashOfRimText.Key("ClashOfRim.ServerEntry.PasswordConfirmation"));
        GUI.SetNextControlName(PasswordConfirmationInputControl);
        passwordConfirmation = GUI.PasswordField(
            new Rect(confirmationLabelRect.xMax + 8f, confirmationLabelRect.y, inRect.width - labelWidth - 8f, 28f),
            passwordConfirmation ?? string.Empty,
            '*');

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

    private void Submit()
    {
        if (string.IsNullOrEmpty(password))
        {
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.ServerEntry.PasswordRequired"),
                MessageTypeDefOf.RejectInput,
                historical: false);
            return;
        }

        if (!string.Equals(password, passwordConfirmation, System.StringComparison.Ordinal))
        {
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.ServerEntry.PasswordMismatch"),
                MessageTypeDefOf.RejectInput,
                historical: false);
            return;
        }

        if (mod.StartMainMenuServerEntryFlow(
                serverBaseUrl,
                userId,
                password,
                createAccountIfMissing: true))
        {
            Close();
        }
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
