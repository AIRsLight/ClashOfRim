using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal sealed class CompatibilityBaselineOverrideWindow : Window
{
    private readonly Func<Action<string, float>?, Action<string, bool>, Task> start;
    private string stage = T("ClashOfRim.Compatibility.Override.StagePreparing");
    private string result = string.Empty;
    private float progress;
    private bool finished;
    private bool success;
    private Task? operation;

    internal CompatibilityBaselineOverrideWindow(Func<Action<string, float>?, Action<string, bool>, Task> start)
    {
        this.start = start;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        forcePause = false;
        doCloseX = false;
        closeOnCancel = false;
    }

    public override Vector2 InitialSize => new(560f, 260f);

    internal static Dialog_MessageBox CreateConfirmation(ClientCompatibilityOverrideIntent intent, Action? onStarted = null)
    {
        string message = T("ClashOfRim.Compatibility.Override.Confirm");
        if (intent.RequiresWarning) message += "\n\n" + T("ClashOfRim.Compatibility.Override.ModListWarning");
        return Dialog_MessageBox.CreateConfirmation(message, () =>
        {
            if (!intent.CanStart) return;
            Find.WindowStack.Add(new CompatibilityBaselineOverrideWindow(
                (progress, completion) => intent.StartAsync(progress, completion) ?? Task.CompletedTask));
            onStarted?.Invoke();
        });
    }

    public override void PreOpen()
    {
        base.PreOpen();
        ServerEntryProgressWindow.CloseCurrent();
        operation = start(UpdateProgress, Finish);
    }

    public override void DoWindowContents(Rect inRect)
    {
        // A discarded/obsolete session cannot publish callbacks. Its own window
        // still needs an exit once the operation ends.
        if (!finished && operation?.IsCompleted == true)
        {
            if (operation.IsFaulted) Log.Warning("[ClashOfRim][Compatibility] Override presentation failed: " + operation.Exception);
            Finish(T("ClashOfRim.Compatibility.Override.Failed"), false);
        }
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), T("ClashOfRim.Compatibility.Override.Title"));
        Text.Font = GameFont.Small;

        Rect messageRect = new(inRect.x, inRect.y + 48f, inRect.width, 72f);
        Widgets.Label(messageRect, finished ? result : stage);

        Rect barRect = new(inRect.x, inRect.y + 132f, inRect.width, 24f);
        Widgets.DrawBox(barRect);
        Rect fillRect = new(barRect.x + 2f, barRect.y + 2f, (barRect.width - 4f) * Mathf.Clamp01(progress), barRect.height - 4f);
        Color barColor = finished
            ? success ? new Color(0.34f, 0.72f, 0.38f) : new Color(0.82f, 0.28f, 0.24f)
            : new Color(0.35f, 0.62f, 0.95f);
        Widgets.DrawBoxSolid(fillRect, barColor);

        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(barRect, Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f) + "%");
        Text.Anchor = TextAnchor.UpperLeft;

        if (!finished)
        {
            return;
        }

        Rect buttonRect = new(inRect.xMax - 130f, inRect.yMax - 38f, 130f, 32f);
        if (Widgets.ButtonText(buttonRect, T("ClashOfRim.Compatibility.Override.ReturnMainMenu")))
        {
            Close();
            GenScene.GoToMainMenu();
        }
    }

    private void UpdateProgress(string message, float value)
    {
        stage = message ?? string.Empty;
        progress = Mathf.Clamp01(value);
    }

    private void Finish(string message, bool accepted)
    {
        result = string.IsNullOrWhiteSpace(message)
            ? accepted ? T("ClashOfRim.Compatibility.Override.Success") : T("ClashOfRim.Compatibility.Override.Failed")
            : message;
        success = accepted;
        finished = true;
        progress = accepted ? 1f : Mathf.Max(progress, 0.95f);
    }

    private static string T(string key)
    {
        return ClashOfRimText.Key(key);
    }
}
