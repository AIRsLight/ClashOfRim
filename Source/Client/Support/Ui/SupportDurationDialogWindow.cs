using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

internal sealed class SupportDurationDialogWindow : Window
{
    private const int MinimumDays = 3;
    private const int MaximumDays = 30;

    private readonly string pawnLabel;
    private readonly string targetLabel;
    private readonly Action<int?, bool> confirmAction;
    private float selectedDays = 7f;
    private bool permanentSupport;

    public SupportDurationDialogWindow(string pawnLabel, string targetLabel, Action<int?, bool> confirmAction)
    {
        this.pawnLabel = pawnLabel;
        this.targetLabel = targetLabel;
        this.confirmAction = confirmAction;
        doCloseX = true;
        closeOnCancel = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
    }

    public override Vector2 InitialSize => new(560f, 300f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Support.DurationDialogTitle"));
        Text.Font = GameFont.Small;

        Widgets.Label(
            new Rect(inRect.x, inRect.y + 42f, inRect.width, 30f),
            ClashOfRimText.Key(
                "ClashOfRim.Support.DurationDialogTarget",
                pawnLabel.Named("PAWN"),
                targetLabel.Named("TARGET")));

        Rect temporaryRect = new(inRect.x, inRect.y + 86f, 170f, 32f);
        if (!permanentSupport)
        {
            Widgets.DrawHighlightSelected(temporaryRect);
        }

        if (Widgets.ButtonText(temporaryRect, ClashOfRimText.Key("ClashOfRim.Support.DurationDialogTemporary")))
        {
            permanentSupport = false;
        }

        Rect permanentRect = new(temporaryRect.xMax + 12f, temporaryRect.y, 170f, 32f);
        if (permanentSupport)
        {
            Widgets.DrawHighlightSelected(permanentRect);
        }

        if (Widgets.ButtonText(permanentRect, ClashOfRimText.Key("ClashOfRim.Support.DurationPermanent")))
        {
            permanentSupport = true;
        }

        string modeText = permanentSupport
            ? ClashOfRimText.Key("ClashOfRim.Support.DurationDialogPermanentSelected")
            : ClashOfRimText.Key("ClashOfRim.Support.DurationDialogTemporarySelected");
        Widgets.Label(new Rect(inRect.x, temporaryRect.yMax + 10f, inRect.width, 24f), modeText);

        int days = Mathf.RoundToInt(selectedDays);
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && !permanentSupport;
        Widgets.Label(
            new Rect(inRect.x, temporaryRect.yMax + 48f, 170f, 24f),
            ClashOfRimText.Key("ClashOfRim.Support.DurationDialogDays", days.Named("DAYS")));
        selectedDays = Widgets.HorizontalSlider(
            new Rect(inRect.x + 180f, temporaryRect.yMax + 48f, inRect.width - 180f, 24f),
            selectedDays,
            MinimumDays,
            MaximumDays,
            roundTo: 1f);
        GUI.enabled = oldEnabled;

        Rect cancelRect = new(inRect.xMax - 216f, inRect.yMax - 38f, 96f, 32f);
        if (Widgets.ButtonText(cancelRect, ClashOfRimText.Key("ClashOfRim.Cancel")))
        {
            Close();
        }

        Rect confirmRect = new(inRect.xMax - 110f, inRect.yMax - 38f, 110f, 32f);
        if (Widgets.ButtonText(confirmRect, ClashOfRimText.Key("ClashOfRim.Support.DurationDialogConfirm")))
        {
            Close();
            confirmAction?.Invoke(permanentSupport ? null : Mathf.RoundToInt(selectedDays), permanentSupport);
        }
    }
}
