using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer;

internal sealed class AllySupportRequestDialogWindow : Window
{
    private const int MaxMessageLength = 500;
    private readonly ClashOfRimMod mod;
    private readonly ModPlayerSummaryDto target;
    private string message = string.Empty;

    public AllySupportRequestDialogWindow(ClashOfRimMod mod, ModPlayerSummaryDto target)
    {
        this.mod = mod;
        this.target = target;
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
    }

    public override Vector2 InitialSize => new(560f, 320f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Diplomacy.SupportRequestTitle"));
        Text.Font = GameFont.Small;

        Widgets.Label(
            new Rect(inRect.x, inRect.y + 42f, inRect.width, 28f),
            ClashOfRimText.Key("ClashOfRim.Diplomacy.SupportRequestTarget", TargetName().Named("PLAYER")));

        Rect messageRect = new(inRect.x, inRect.y + 82f, inRect.width, 132f);
        message = Widgets.TextArea(messageRect, message ?? string.Empty);
        if (message.Length > MaxMessageLength)
        {
            message = message.Substring(0, MaxMessageLength);
        }

        Text.Font = GameFont.Tiny;
        Widgets.Label(
            new Rect(inRect.x, messageRect.yMax + 6f, inRect.width, 22f),
            ClashOfRimText.Key("ClashOfRim.Diplomacy.SupportRequestHint", MaxMessageLength.Named("COUNT")));
        Text.Font = GameFont.Small;

        Rect cancelRect = new(inRect.xMax - 204f, inRect.yMax - 36f, 96f, 32f);
        if (Widgets.ButtonText(cancelRect, ClashOfRimText.Key("ClashOfRim.Cancel")))
        {
            Close();
        }

        Rect sendRect = new(inRect.xMax - 100f, inRect.yMax - 36f, 100f, 32f);
        if (Widgets.ButtonText(sendRect, ClashOfRimText.Key("ClashOfRim.Send")))
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
        mod.StartCreateDiplomacyEvent("SupportRequest", target, message);
        Close();
    }

    private string TargetName()
    {
        return string.IsNullOrWhiteSpace(target.UserId)
            ? ClashOfRimText.Key("ClashOfRim.UnknownPlayer")
            : target.UserId;
    }
}
