using RimWorld;
using System;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

public sealed class ClashOfRimServerEntryDialog : Window
{
    private const string DefaultServerBaseUrl = "http://127.0.0.1:5000";
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
        serverBaseUrl = Widgets.TextField(serverInputRect, serverBaseUrl ?? string.Empty);

        Rect inputLabelRect = new(inRect.x, inRect.y + 82f, LabelWidth, 30f);
        Widgets.Label(inputLabelRect, ClashOfRimText.Key("ClashOfRim.UserId"));
        Rect inputRect = new(inRect.x + InputOffset, inputLabelRect.y, inRect.width - InputOffset, 30f);
        userId = Widgets.TextField(inputRect, userId ?? string.Empty);

        Rect passwordLabelRect = new(inRect.x, inRect.y + 122f, LabelWidth, 30f);
        Widgets.Label(passwordLabelRect, ClashOfRimText.Key("ClashOfRim.Password"));
        Rect passwordRect = new(inRect.x + InputOffset, passwordLabelRect.y, inRect.width - InputOffset, 30f);
        password = GUI.PasswordField(passwordRect, password ?? string.Empty, '*');

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

    private void Submit()
    {
        if (mod.StartMainMenuServerEntryFlow(serverBaseUrl, userId, password))
        {
            Close();
        }
    }
}
