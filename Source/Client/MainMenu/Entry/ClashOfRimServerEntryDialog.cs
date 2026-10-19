using AIRsLight.ClashOfRim.Modules.World;
using System;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

public sealed class ClashOfRimServerEntryDialog : Window
{
    private const string ServerAddressInputControl = "ClashOfRim.ServerEntry.ServerAddress";
    private const string UserIdInputControl = "ClashOfRim.ServerEntry.UserId";
    private const string PasswordInputControl = "ClashOfRim.ServerEntry.Password";
    private const float LabelWidth = 130f;
    private const float InputOffset = LabelWidth + 10f;
    private readonly ClientWorldEntryForm form;

    // Preserve the public integration constructor; runtime UI uses the form overload.
    public ClashOfRimServerEntryDialog(ClashOfRimMod mod)
        : this((mod ?? throw new ArgumentNullException(nameof(mod))).CreateServerEntryForm()) { }

    internal ClashOfRimServerEntryDialog(ClientWorldEntryForm form)
    {
        this.form = form;
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
    }

    public override Vector2 InitialSize => new(520f, 250f);

    public override void DoWindowContents(Rect inRect)
    {
        if (!form.IsCurrent) { Close(); return; }
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 28f), ClashOfRimText.Key("ClashOfRim.ServerEntry.JoinTitle"));

        Rect serverLabelRect = new(inRect.x, inRect.y + 42f, LabelWidth, 30f);
        Widgets.Label(serverLabelRect, ClashOfRimText.Key("ClashOfRim.ServerAddress"));
        Rect serverInputRect = new(inRect.x + InputOffset, serverLabelRect.y, inRect.width - InputOffset, 30f);
        GUI.SetNextControlName(ServerAddressInputControl);
        form.ServerBaseUrl = Widgets.TextField(serverInputRect, form.ServerBaseUrl);

        Rect inputLabelRect = new(inRect.x, inRect.y + 82f, LabelWidth, 30f);
        Widgets.Label(inputLabelRect, ClashOfRimText.Key("ClashOfRim.UserId"));
        Rect inputRect = new(inRect.x + InputOffset, inputLabelRect.y, inRect.width - InputOffset, 30f);
        GUI.SetNextControlName(UserIdInputControl);
        form.UserId = Widgets.TextField(inputRect, form.UserId);

        Rect passwordLabelRect = new(inRect.x, inRect.y + 122f, LabelWidth, 30f);
        Widgets.Label(passwordLabelRect, ClashOfRimText.Key("ClashOfRim.Password"));
        Rect passwordRect = new(inRect.x + InputOffset, passwordLabelRect.y, inRect.width - InputOffset, 30f);
        GUI.SetNextControlName(PasswordInputControl);
        form.Password = GUI.PasswordField(passwordRect, form.Password, '*');

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

    public override void PostClose()
    {
        form.Close();
        base.PostClose();
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
        if (form.Submit())
        {
            Close();
        }
    }
}
