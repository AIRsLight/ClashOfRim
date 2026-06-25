using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class ServerEntryProgressWindow : Window
{
    private string title;
    private string message;
    private float progress;
    private bool indeterminate;
    private bool canClose;

    public ServerEntryProgressWindow(string title, string message, float progress, bool canClose)
    {
        this.title = title ?? string.Empty;
        this.message = message ?? string.Empty;
        indeterminate = progress < 0f;
        this.progress = Mathf.Clamp01(progress);
        this.canClose = canClose;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        forcePause = false;
        UpdateCloseState();
    }

    public override Vector2 InitialSize => new(560f, 240f);

    public void UpdateStatus(string newTitle, string newMessage, float newProgress, bool allowClose)
    {
        title = newTitle ?? string.Empty;
        message = newMessage ?? string.Empty;
        indeterminate = newProgress < 0f;
        progress = Mathf.Clamp01(newProgress);
        canClose = allowClose;
        UpdateCloseState();
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), title);
        Text.Font = GameFont.Small;

        Rect messageRect = new(inRect.x, inRect.y + 48f, inRect.width, 70f);
        Widgets.Label(messageRect, message);

        Rect barRect = new(inRect.x, inRect.y + 126f, inRect.width, 24f);
        Widgets.DrawBox(barRect);
        Rect fillRect = indeterminate
            ? MovingFillRect(barRect)
            : new Rect(barRect.x + 2f, barRect.y + 2f, (barRect.width - 4f) * progress, barRect.height - 4f);
        Widgets.DrawBoxSolid(fillRect, new Color(0.35f, 0.62f, 0.95f));

        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(
            barRect,
            indeterminate
                ? ClashOfRimText.Key("ClashOfRim.ServerEntry.ProgressWaiting")
                : ClashOfRimText.Key(
                    "ClashOfRim.ServerEntry.Progress",
                    Mathf.RoundToInt(progress * 100f).Named("PERCENT")));
        Text.Anchor = TextAnchor.UpperLeft;

        if (!canClose)
        {
            return;
        }

        Rect closeRect = new(inRect.xMax - 110f, inRect.yMax - 38f, 110f, 32f);
        if (Widgets.ButtonText(closeRect, ClashOfRimText.Key("ClashOfRim.Close")))
        {
            Close();
        }
    }

    private void UpdateCloseState()
    {
        doCloseX = canClose;
        closeOnCancel = canClose;
    }

    private static Rect MovingFillRect(Rect barRect)
    {
        float innerWidth = barRect.width - 4f;
        float segmentWidth = Mathf.Max(80f, innerWidth * 0.28f);
        float travel = Mathf.Max(1f, innerWidth - segmentWidth);
        float phase = Mathf.PingPong(Time.realtimeSinceStartup * 140f, travel);
        return new Rect(barRect.x + 2f + phase, barRect.y + 2f, segmentWidth, barRect.height - 4f);
    }
}
