using System;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.Multiplayer.Pages.MultiplayerAdminLayout;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerAdminAuditPage
{
    private readonly ClientAdminAuditPanel model;
    private object? displayed;
    private Vector2 scroll;
    internal MultiplayerAdminAuditPage(ClientAdminAuditPanel model) => this.model = model;
    internal void Close() { model.Close(); displayed = null; scroll = default; }
    internal void Draw(Rect rect)
    {
        var audits = model.Read();
        var owner = model.Ownership;
        if (!ReferenceEquals(displayed, owner)) { displayed = owner; scroll = default; }
        Rect view = BeginAdminScroll(rect, ref scroll, Math.Max(rect.height, audits.Count * 42f + 20f));
        float y = 0f;
        foreach (var audit in audits)
        {
            Widgets.Label(new Rect(0f, y, view.width, 22f), $"{audit.CreatedAtUtc}  {audit.ActorUserId}  {audit.ActionKind}  {audit.TargetUserId}");
            if (!string.IsNullOrWhiteSpace(audit.Message))
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(0f, y + 20f, view.width, 18f), audit.Message);
                Text.Font = GameFont.Small;
            }

            y += 42f;
        }

        if (audits.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, view.width, 32f), ClashOfRimText.Key("ClashOfRim.Admin.NoAudit"));
        }

        Widgets.EndScrollView();
    }

}
