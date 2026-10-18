using AIRsLight.ClashOfRim.Modules.Multiplayer;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerAccountPage
{
    private readonly ClientAccountPanel model;
    internal MultiplayerAccountPage(ClientAccountPanel model) => this.model = model;
    internal void Close() => model.Close();

    internal void Draw(Rect rect)
    {
        var draft = model.Read();
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAccount"));
        Text.Font = GameFont.Small;

        float y = inner.y + 44f;
        Widgets.Label(new Rect(inner.x, y, inner.width, 24f), ClashOfRimText.Key(
            "ClashOfRim.Account.CurrentUser", model.UserId.Named("USER")));
        y += 38f;

        Widgets.Label(new Rect(inner.x, y, 130f, 24f), ClashOfRimText.Key("ClashOfRim.Account.CurrentPassword"));
        draft.CurrentPassword = GUI.PasswordField(new Rect(inner.x + 140f, y - 2f, 260f, 28f), draft.CurrentPassword, '*');
        y += 36f;

        Widgets.Label(new Rect(inner.x, y, 130f, 24f), ClashOfRimText.Key("ClashOfRim.Account.NewPassword"));
        draft.NewPassword = GUI.PasswordField(new Rect(inner.x + 140f, y - 2f, 260f, 28f), draft.NewPassword, '*');
        y += 42f;

        if (Widgets.ButtonText(new Rect(inner.x + 140f, y, 160f, 30f), ClashOfRimText.Key("ClashOfRim.Account.ChangePassword"), active: !model.Busy))
            _ = model.SubmitAsync(draft);

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, y + 44f, inner.width, 40f), model.Status);
        Text.Font = GameFont.Small;
    }
}
