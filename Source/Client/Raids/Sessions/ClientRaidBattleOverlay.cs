using System;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidBattleOverlay
{
    internal static ClientRaidBattleOverlay? Current { get; private set; }
    internal static void Attach(ClientRaidBattleOverlay? value) => Current = value;
    private readonly ClientRaidFinishActions actions;
    private readonly Func<string?> readStatus;
    internal ClientRaidBattleOverlay(ClientRaidFinishActions actions, Func<string?> readStatus) { this.actions = actions; this.readStatus = readStatus; }
    internal void Draw(Game game, ActiveRemoteMapSession? remote, ActiveRaidBattleSession? battle)
    {
        if (!ReferenceEquals(Current, this) || !ReferenceEquals(game, Verse.Current.Game) || remote?.IsRaidBattle != true || battle is null
            || !ReferenceEquals(remote, ClashOfRimGameComponent.ActiveRemoteMapSession) || !ReferenceEquals(battle, ClashOfRimGameComponent.ActiveRaidBattleSession)
            || Verse.Current.ProgramState != ProgramState.Playing || WorldRendererUtility.WorldSelected) return;
        bool expired = battle.IsExpired, finishing = battle.FinishInProgress;
        var finish = expired || finishing ? null : actions.Prepare(battle, "ManualFinish", confirmation: true);
        Rect rect = new(UI.screenWidth - 328f, 12f, 316f, 108f);
        Widgets.DrawWindowBackground(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft;
        string status;
        if (expired || finishing)
        {
            status = readStatus() ?? "";
            if (string.IsNullOrWhiteSpace(status)) status = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementUploading");
        }
        else status = ClashOfRimText.Key("ClashOfRim.Raid.BattleCountdown", FormatRemaining(battle.Remaining).Named("TIME"));
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), status);
        string target = string.IsNullOrWhiteSpace(battle.DefenderUserId) ? battle.DefenderColonyId : battle.DefenderUserId;
        Widgets.Label(new Rect(inner.x, inner.y + 26f, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Raid.BattleTarget", target.Named("TARGET")));
        if (finish is null) GUI.color = Color.gray;
        if (Widgets.ButtonText(new Rect(inner.x, inner.y + 58f, inner.width, 30f), ClashOfRimText.Key("ClashOfRim.Raid.FinishBattle")) && finish is not null) finish();
        GUI.color = Color.white; Text.Anchor = TextAnchor.UpperLeft;
    }
    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "00:00";
        int totalSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        int hours = totalSeconds / 3600, minutes = (totalSeconds % 3600) / 60, seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours:00}:{minutes:00}:{seconds:00}" : $"{minutes:00}:{seconds:00}";
    }
}
