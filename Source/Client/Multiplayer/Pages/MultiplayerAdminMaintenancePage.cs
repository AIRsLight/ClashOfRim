using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.Multiplayer.Pages.MultiplayerAdminLayout;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerAdminMaintenancePage
{
    private readonly ClientAdminMaintenancePanel model;
    private ClientAdminMaintenancePanel.Draft? displayed;
    private Vector2 scroll;
    internal MultiplayerAdminMaintenancePage(ClientAdminMaintenancePanel model) => this.model = model;
    internal void Close() { model.Close(); displayed = null; scroll = default; }

    internal void Draw(Rect rect)
    {
        var draft = model.Read();
        if (!ReferenceEquals(displayed, draft)) { displayed = draft; scroll = default; }
        Rect view = BeginAdminScroll(rect, ref scroll, 300f);
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionBroadcast"));
        Widgets.Label(new Rect(0f, y, 160f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.BroadcastMessage"));
        draft.Message = Widgets.TextField(new Rect(170f, y, view.width - 430f, 28f), draft.Message);
        if (DrawAdminSelectionButton(
                new Rect(view.width - 250f, y, 110f, 28f),
                SeverityLabel(draft.Severity),
                !model.Busy))
        {
            OpenSeverity(draft);
        }

        if (Widgets.ButtonText(new Rect(view.width - 120f, y, 110f, 28f), ClashOfRimText.Key("ClashOfRim.Admin.ActionBroadcast"), active: model.CanBroadcast(draft)))
        {
            _ = model.BroadcastAsync(draft);
        }

        y += 36f;
        Widgets.Label(new Rect(0f, y, 160f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.BroadcastTarget"));
        if (DrawAdminSelectionButton(
                new Rect(170f, y, 220f, 28f),
                draft.Target?.DisplayName ?? ClashOfRimText.Key("ClashOfRim.Admin.BroadcastTargetAll"),
                !model.Busy))
        {
            OpenTarget(draft);
        }

        Widgets.CheckboxLabeled(
            new Rect(410f, y, view.width - 410f, 28f),
            ClashOfRimText.Key("ClashOfRim.Admin.BroadcastPersistent"),
            ref draft.Persistent);

        y += 46f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionMaintenance"));
        Widgets.Label(new Rect(0f, y, view.width, 24f), ClashOfRimText.Key(model.LoginLocked switch
        {
            true => "ClashOfRim.Admin.MaintenanceLocked",
            false => "ClashOfRim.Admin.MaintenanceUnlocked",
            null => "ClashOfRim.Admin.NoStatus"
        }));
        y += 30f;
        Widgets.Label(new Rect(0f, y, 160f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.MaintenanceReason"));
        draft.Reason = Widgets.TextField(new Rect(170f, y, view.width - 300f, 28f), draft.Reason);
        if (Widgets.ButtonText(new Rect(view.width - 120f, y, 110f, 28f), ClashOfRimText.Key("ClashOfRim.Admin.ActionLockLogin"), active: model.CanSubmit(draft)))
        {
            _ = model.SetMaintenanceAsync(draft, true);
        }

        y += 38f;
        if (Widgets.ButtonText(new Rect(170f, y, 160f, 30f), ClashOfRimText.Key("ClashOfRim.Admin.ActionUnlockLogin"), active: model.CanSubmit(draft)))
        {
            _ = model.SetMaintenanceAsync(draft, false);
        }

        Widgets.EndScrollView();
    }

    private void OpenSeverity(ClientAdminMaintenancePanel.Draft draft)
    {
        var choose = model.ChooseSeverity(draft);
        var options = new List<FloatMenuOption>();
        foreach (ClientAdminBroadcastSeverity severity in Enum.GetValues(typeof(ClientAdminBroadcastSeverity)))
            options.Add(new(SeverityLabel(severity), () => choose(severity)));
        Find.WindowStack.Add(new FloatMenu(options));
    }
    private void OpenTarget(ClientAdminMaintenancePanel.Draft draft)
    {
        var choose = model.ChooseTarget(draft);
        var options = new List<FloatMenuOption>
        {
            new(ClashOfRimText.Key("ClashOfRim.Admin.BroadcastTargetAll"), () => choose(null))
        };
        foreach (var account in model.Targets()) options.Add(new(account.DisplayName, () => choose(account)));
        Find.WindowStack.Add(new FloatMenu(options));
    }
    private static string SeverityLabel(ClientAdminBroadcastSeverity severity) => ClashOfRimText.Key(severity switch
    {
        ClientAdminBroadcastSeverity.Warning => "ClashOfRim.Admin.BroadcastSeverityWarning",
        ClientAdminBroadcastSeverity.Critical => "ClashOfRim.Admin.BroadcastSeverityError",
        _ => "ClashOfRim.Admin.BroadcastSeverityInfo"
    });
}
