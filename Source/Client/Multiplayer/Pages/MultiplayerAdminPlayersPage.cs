using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using RimWorld;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.Multiplayer.Pages.MultiplayerAdminLayout;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerAdminPlayersPage
{
    private readonly ClientAdminPlayerPanel model;
    private Vector2 scroll;
    private object? owner;
    internal MultiplayerAdminPlayersPage(ClientAdminPlayerPanel model) => this.model = model;
    internal void Close() { model.Close(); scroll = default; owner = null; }

    internal void Draw(Rect rect)
    {
        var current = model.Ownership;
        if (!ReferenceEquals(owner, current)) { owner = current; scroll = default; }
        var players = model.Read();
        Rect view = BeginAdminScroll(rect, ref scroll, Math.Max(rect.height, players.Count * 70f + 20f));
        float y = 0f;
        if (players.Count == 0)
        {
            Widgets.Label(new Rect(0f, y, view.width, 32f), ClashOfRimText.Key("ClashOfRim.Admin.NoPlayers"));
        }

        foreach (var player in players)
        {
            Rect row = new(0f, y, view.width, 64f);
            Widgets.DrawMenuSection(row);
            Rect inner = row.ContractedBy(6f);
            string flags = (player.Online ? ClashOfRimText.Key("ClashOfRim.Multiplayer.Online") : ClashOfRimText.Key("ClashOfRim.Multiplayer.Offline"))
                + (player.IsAdministrator ? "  " + ClashOfRimText.Key("ClashOfRim.Admin.AdminFlag") : string.Empty)
                + (player.IsBanned ? "  " + ClashOfRimText.Key("ClashOfRim.Admin.BannedFlag") : string.Empty);
            string displayName = player.DisplayName;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width - 520f, 24f), displayName);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inner.x, inner.y + 26f, inner.width - 520f, 18f), flags + "  " + player.UserId);
            Text.Font = GameFont.Small;

            float x = inner.xMax - 150f;
            bool isCurrentPlayer = player.IsSelf;
            if (isCurrentPlayer)
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(x - 120f, inner.y + 14f, 260f, 24f), ClashOfRimText.Key("ClashOfRim.Multiplayer.CurrentPlayer"));
                Text.Font = GameFont.Small;
                y += 70f;
                continue;
            }

            if (Widgets.ButtonText(
                    new Rect(x, inner.y + 10f, 140f, 30f),
                    ClashOfRimText.Key("ClashOfRim.Admin.PlayerManage"),
                    active: model.CanManage(player)))
            {
                OpenActions(player);
            }

            y += 70f;
        }

        Widgets.EndScrollView();
    }

    private void OpenActions(ClientAdminPlayerPanel.Row player)
    {
        var options = new List<FloatMenuOption>();
        foreach (var intent in model.PrepareActions(player))
        {
            string label = ClashOfRimText.Key("ClashOfRim.Admin.Action" +
                (intent.Kind == ClientAdminPlayerAction.ResetOfflinePassword ? "ResetPassword" : intent.Kind.ToString()));
            options.Add(new FloatMenuOption(label, () =>
            {
                if (!intent.IsCurrent) return;
                if (intent.Kind == ClientAdminPlayerAction.ResetOfflinePassword)
                {
                    if (model.OpenPassword(intent) is { } draft) Find.WindowStack.Add(new AdminResetPasswordDialogWindow(draft));
                    return;
                }
                string confirmation = intent.Kind == ClientAdminPlayerAction.DeletePlayerSave
                    ? ClashOfRimText.Key("ClashOfRim.Admin.ConfirmDeletePlayerSave", player.UserId.Named("USER"))
                    : ClashOfRimText.Key("ClashOfRim.Admin.ConfirmAction", label.Named("ACTION"), player.UserId.Named("USER"));
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(confirmation, () => _ = intent.ExecuteAsync()));
            }));
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }
}
