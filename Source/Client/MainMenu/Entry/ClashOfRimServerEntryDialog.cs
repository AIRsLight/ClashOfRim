using RimWorld;
using System;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

public sealed class ClashOfRimServerEntryDialog : Window
{
    private const string DefaultServerBaseUrl = "http://127.0.0.1:5000";
    private const string ServerAddressInputControl = "ClashOfRim.ServerEntry.ServerAddress";
    private const string UserIdInputControl = "ClashOfRim.ServerEntry.UserId";
    private const string PasswordInputControl = "ClashOfRim.ServerEntry.Password";
    private const float LabelWidth = 130f;
    private const float InputOffset = LabelWidth + 10f;
    private readonly ClashOfRimMod mod;
    private string serverBaseUrl;
    private string userId;
    private string password;

    public ClashOfRimServerEntryDialog(ClashOfRimMod mod)
    {
        if (mod is null)
        {
            throw new ArgumentNullException(nameof(mod));
        }

        this.mod = mod;
        serverBaseUrl = string.IsNullOrWhiteSpace(mod.ServerBaseUrl) ? DefaultServerBaseUrl : mod.ServerBaseUrl;
        userId = mod.UserId ?? string.Empty;
        password = string.Empty;
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
    }

    public override Vector2 InitialSize => new(520f, 250f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 28f), ClashOfRimText.Key("ClashOfRim.ServerEntry.JoinTitle"));

        Rect serverLabelRect = new(inRect.x, inRect.y + 42f, LabelWidth, 30f);
        Widgets.Label(serverLabelRect, ClashOfRimText.Key("ClashOfRim.ServerAddress"));
        Rect serverInputRect = new(inRect.x + InputOffset, serverLabelRect.y, inRect.width - InputOffset, 30f);
        GUI.SetNextControlName(ServerAddressInputControl);
        serverBaseUrl = Widgets.TextField(serverInputRect, serverBaseUrl ?? string.Empty);

        Rect inputLabelRect = new(inRect.x, inRect.y + 82f, LabelWidth, 30f);
        Widgets.Label(inputLabelRect, ClashOfRimText.Key("ClashOfRim.UserId"));
        Rect inputRect = new(inRect.x + InputOffset, inputLabelRect.y, inRect.width - InputOffset, 30f);
        GUI.SetNextControlName(UserIdInputControl);
        userId = Widgets.TextField(inputRect, userId ?? string.Empty);

        Rect passwordLabelRect = new(inRect.x, inRect.y + 122f, LabelWidth, 30f);
        Widgets.Label(passwordLabelRect, ClashOfRimText.Key("ClashOfRim.Password"));
        Rect passwordRect = new(inRect.x + InputOffset, passwordLabelRect.y, inRect.width - InputOffset, 30f);
        GUI.SetNextControlName(PasswordInputControl);
        password = GUI.PasswordField(passwordRect, password ?? string.Empty, '*');

        HandleTabFocus();

        Rect cancelRect = new(inRect.xMax - 204f, inRect.yMax - 36f, 96f, 32f);
        if (Widgets.ButtonText(cancelRect, ClashOfRimText.Key("ClashOfRim.Cancel")))
        {
            Close();
        }

        Rect startRect = new(inRect.xMax - 100f, inRect.yMax - 36f, 100f, 32f);
        if (Widgets.ButtonText(startRect, ClashOfRimText.Key("ClashOfRim.Start")))
        {
            Submit();
        }
    }

    public override void OnAcceptKeyPressed()
    {
        Submit();
        Event.current?.Use();
    }

    private static void HandleTabFocus()
    {
        if (Event.current is not { type: EventType.KeyDown, keyCode: KeyCode.Tab })
        {
            return;
        }

        GUI.FocusControl(ResolveNextInputControl(
            GUI.GetNameOfFocusedControl(),
            Event.current.shift));
        Event.current.Use();
    }

    private static string ResolveNextInputControl(string currentControl, bool reverse)
    {
        if (reverse)
        {
            return currentControl switch
            {
                ServerAddressInputControl => PasswordInputControl,
                UserIdInputControl => ServerAddressInputControl,
                PasswordInputControl => UserIdInputControl,
                _ => PasswordInputControl
            };
        }

        return currentControl switch
        {
            ServerAddressInputControl => UserIdInputControl,
            UserIdInputControl => PasswordInputControl,
            PasswordInputControl => ServerAddressInputControl,
            _ => ServerAddressInputControl
        };
    }

    private void Submit()
    {
        if (mod.StartMainMenuServerEntryFlow(serverBaseUrl, userId, password))
        {
            Close();
        }
    }
}
