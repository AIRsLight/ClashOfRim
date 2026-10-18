using System;
using System.Collections.Generic;
using System.Globalization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Multiplayer.Players;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.Multiplayer.Players.MultiplayerPlayerText;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerDiplomacyPage
{
    private const float DiplomacyRowHeight = 70f;
    private readonly ClientPlayerPanel players;
    private readonly PlayerColonyLocator locations;
    private Vector2 diplomacyScrollPosition;
    private Vector2 diplomacyScoreboardScrollPosition;
    private object? owner;
    private ClientPlayerPanelView View => players.Read(ClashOfRimText.Key("ClashOfRim.UnknownPlayer"));

    internal MultiplayerDiplomacyPage(ClientPlayerPanel players, PlayerColonyLocator locations)
    { this.players = players; this.locations = locations; }

    internal void Reset()
    {
        owner = null; diplomacyScrollPosition = diplomacyScoreboardScrollPosition = Vector2.zero;
        locations.Reset();
    }

    private void EnsureCurrent()
    {
        var current = players.Ownership;
        if (ReferenceEquals(owner, current)) return;
        Reset(); owner = current;
    }

    internal void Draw(Rect rect)
    {
        EnsureCurrent();
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabDiplomacy"));
        Text.Font = GameFont.Small;

        IReadOnlyList<ModPlayerSummaryDto> players = View.Players;
        float contentY = inner.y + 42f;
        float contentHeight = inner.height - 42f;
        const float gap = 10f;
        float scoreboardWidth = Mathf.Min(340f, Mathf.Max(260f, inner.width * 0.34f));
        Rect listPanelRect = new(inner.x, contentY, inner.width - scoreboardWidth - gap, contentHeight);
        Rect scoreboardPanelRect = new(listPanelRect.xMax + gap, contentY, scoreboardWidth, contentHeight);

        Widgets.DrawMenuSection(listPanelRect);
        Rect listInner = listPanelRect.ContractedBy(8f);
        Rect outRect = new(listInner.x, listInner.y, listInner.width, listInner.height);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, players.Count * DiplomacyRowHeight));
        Widgets.BeginScrollView(outRect, ref diplomacyScrollPosition, viewRect);
        if (players.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Multiplayer.NoPlayers"));
        }
        else
        {
            for (int index = 0; index < players.Count; index++)
            {
                DrawDiplomacyRow(new Rect(0f, index * DiplomacyRowHeight, viewRect.width, DiplomacyRowHeight - 4f), players[index]);
            }
        }

        Widgets.EndScrollView();

        DrawWealthScoreboard(scoreboardPanelRect);
    }

    private void DrawWealthScoreboard(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Scoreboard.Title"));

        Rect outRect = new(inner.x, inner.y + 30f, inner.width, inner.height - 30f);
        IReadOnlyList<ModPlayerSummaryDto> rankedPlayers = View.WealthRanked;
        const float rowHeight = 28f;
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, rowHeight * (rankedPlayers.Count + 1) + 18f));

        Widgets.BeginScrollView(outRect, ref diplomacyScoreboardScrollPosition, viewRect);
        Text.Font = GameFont.Tiny;
        float y = 0f;
        Widgets.Label(new Rect(0f, y, 34f, 22f), ClashOfRimText.Key("ClashOfRim.Scoreboard.RankHeader"));
        Widgets.Label(new Rect(38f, y, viewRect.width - 130f, 22f), ClashOfRimText.Key("ClashOfRim.Scoreboard.PlayerHeader"));
        Text.Anchor = TextAnchor.UpperRight;
        Widgets.Label(new Rect(viewRect.width - 90f, y, 90f, 22f), ClashOfRimText.Key("ClashOfRim.Scoreboard.WealthHeader"));
        Text.Anchor = TextAnchor.UpperLeft;
        Widgets.DrawLineHorizontal(0f, y + 24f, viewRect.width);

        if (rankedPlayers.Count == 0)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, y + rowHeight, viewRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Multiplayer.NoPlayers"));
            y += rowHeight * 2f;
        }
        else
        {
            for (int index = 0; index < rankedPlayers.Count; index++)
            {
                DrawWealthScoreboardRow(new Rect(0f, y + rowHeight * (index + 1), viewRect.width, rowHeight), index + 1, rankedPlayers[index]);
            }

            y += rowHeight * (rankedPlayers.Count + 1);
        }

        Text.Font = GameFont.Small;
        Widgets.EndScrollView();
    }

    private void DrawWealthScoreboardRow(Rect rect, int rank, ModPlayerSummaryDto player)
    {
        bool isSelf = View.IsSelf(player);
        if (isSelf)
        {
            Widgets.DrawHighlightSelected(rect);
        }
        else
        {
            Widgets.DrawHighlightIfMouseover(rect);
        }

        Text.Font = GameFont.Tiny;
        Color previousColor = GUI.color;
        if (isSelf)
        {
            GUI.color = new Color(1f, 0.86f, 0.35f);
        }

        Widgets.Label(new Rect(rect.x, rect.y + 4f, 34f, 22f), rank.ToString(CultureInfo.InvariantCulture));
        Widgets.Label(new Rect(rect.x + 38f, rect.y + 4f, rect.width - 132f, 22f), PlayerDisplayName(player));
        Text.Anchor = TextAnchor.UpperRight;
        Widgets.Label(new Rect(rect.xMax - 90f, rect.y + 4f, 90f, 22f), FormatPlayerWealth(player.LatestSnapshotWealth));
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = previousColor;
        Text.Font = GameFont.Small;
    }

    private void DrawDiplomacyRow(Rect rect, ModPlayerSummaryDto player)
    {
        Widgets.DrawHighlightIfMouseover(rect);
        bool isSelf = View.IsSelf(player);
        string online = View.IsOnline(player)
            ? ClashOfRimText.Key("ClashOfRim.Multiplayer.Online")
            : ClashOfRimText.Key("ClashOfRim.Multiplayer.Offline");
        string self = isSelf ? "  " + ClashOfRimText.Key("ClashOfRim.Multiplayer.CurrentPlayer") : string.Empty;
        string relationKind = NormalizeDiplomacyRelationKind(player.RelationKind);
        string relation = FormatDiplomacyRelationKind(relationKind);

        const float buttonWidth = 112f;
        const float buttonHeight = 30f;
        const float buttonGap = 8f;
        int diplomacyButtonCount = !isSelf && !players.Busy
            ? relationKind is "Neutral" or "Ally" ? 2 : 1
            : 0;
        int buttonCount = !isSelf && !players.Busy
            ? diplomacyButtonCount + 1
            : 0;
        float buttonAreaWidth = buttonCount > 0
            ? buttonCount * buttonWidth + Math.Max(0, buttonCount - 1) * buttonGap
            : 0f;
        float textWidth = Math.Max(120f, rect.width - buttonAreaWidth - 24f);
        Rect nameRect = new(rect.x + 8f, rect.y + 5f, textWidth, 22f);
        Rect statusRect = new(rect.x + 8f, rect.y + 28f, textWidth, 20f);
        TradeUiUtility.DrawTruncatedLabel(nameRect, PlayerDisplayName(player));
        Text.Font = GameFont.Tiny;
        TradeUiUtility.DrawTruncatedLabel(statusRect, $"{online}  {relation}{self}");
        if (Prefs.DevMode)
        {
            string debugLine = $"{player.UserId}/{player.ColonyId}  "
                + (string.IsNullOrWhiteSpace(player.CurrentSnapshotId) ? ClashOfRimText.Key("ClashOfRim.CurrentSnapshotMissing") : player.CurrentSnapshotId!);
            TradeUiUtility.DrawTruncatedLabel(new Rect(rect.x + 8f, rect.y + 48f, textWidth, 18f), debugLine);
        }
        Text.Font = GameFont.Small;

        if (isSelf || players.Busy)
        {
            return;
        }

        float buttonY = rect.y + (rect.height - buttonHeight) * 0.5f;
        float buttonX = rect.xMax - buttonCount * buttonWidth - Math.Max(0, buttonCount - 1) * buttonGap;
        if (relationKind == "Neutral")
        {
            if (Widgets.ButtonText(new Rect(buttonX, buttonY, buttonWidth, buttonHeight), ClashOfRimText.Key("ClashOfRim.Diplomacy.RequestAlliance")))
            {
                ConfirmDiplomacyEvent("AllianceRequest", player);
            }

            buttonX += buttonWidth + buttonGap;
        }

        if (relationKind == "Ally")
        {
            if (Widgets.ButtonText(new Rect(buttonX, buttonY, buttonWidth, buttonHeight), ClashOfRimText.Key("ClashOfRim.Diplomacy.CancelAlliance")))
            {
                ConfirmDiplomacyEvent("AllianceCancellation", player);
            }

            buttonX += buttonWidth + buttonGap;

            if (Widgets.ButtonText(new Rect(buttonX, buttonY, buttonWidth, buttonHeight), ClashOfRimText.Key("ClashOfRim.Diplomacy.RequestSupport")))
            {
                Find.WindowStack.Add(new AllySupportRequestDialogWindow(player.UserId, players.PrepareDiplomacy("SupportRequest", player)));
            }

            buttonX += buttonWidth + buttonGap;
        }
        else if (relationKind == "Neutral")
        {
            if (Widgets.ButtonText(new Rect(buttonX, buttonY, buttonWidth, buttonHeight), ClashOfRimText.Key("ClashOfRim.Diplomacy.DeclareWar")))
            {
                ConfirmDiplomacyEvent("WarDeclaration", player);
            }

            buttonX += buttonWidth + buttonGap;
        }

        if (relationKind == "Hostile")
        {
            if (Widgets.ButtonText(new Rect(buttonX, buttonY, buttonWidth, buttonHeight), ClashOfRimText.Key("ClashOfRim.Diplomacy.RequestPeace")))
            {
                ConfirmDiplomacyEvent("PeaceRequest", player);
            }

            buttonX += buttonWidth + buttonGap;
        }

        Rect locateRect = new(buttonX, buttonY, buttonWidth, buttonHeight);
        bool canLocate = locations.CanLocate(player, View.Version);
        if (Widgets.ButtonText(locateRect, ClashOfRimText.Key("ClashOfRim.Multiplayer.LocatePlayer"), active: canLocate))
        {
            locations.Locate(player, View.Version);
        }

        if (!canLocate)
        {
            TooltipHandler.TipRegion(locateRect, ClashOfRimText.Key("ClashOfRim.Multiplayer.PlayerLocationUnavailable"));
        }
    }

    private void ConfirmDiplomacyEvent(string kind, ModPlayerSummaryDto player)
    {
        string key = kind switch
        {
            "AllianceRequest" => "ClashOfRim.Diplomacy.ConfirmAllianceRequest",
            "AllianceCancellation" => "ClashOfRim.Diplomacy.ConfirmAllianceCancellation",
            "WarDeclaration" => "ClashOfRim.Diplomacy.ConfirmWarDeclaration",
            "PeaceRequest" => "ClashOfRim.Diplomacy.ConfirmPeaceRequest",
            _ => "ClashOfRim.Diplomacy.ConfirmEvent"
        };

        string message = ClashOfRimText.Key(
            key,
            PlayerDisplayName(player).Named("PLAYER"),
            kind.Named("KIND"));
        var send = players.PrepareDiplomacy(kind, player);
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(message, () => _ = send(null)));
    }

    private static string FormatPlayerWealth(int? wealth)
    {
        return wealth.HasValue
            ? wealth.Value.ToString("N0", CultureInfo.InvariantCulture)
            : ClashOfRimText.Key("ClashOfRim.Scoreboard.WealthUnknown");
    }

    private static string NormalizeDiplomacyRelationKind(string? relationKind)
    {
        if (string.Equals(relationKind, "Ally", StringComparison.OrdinalIgnoreCase))
        {
            return "Ally";
        }

        if (string.Equals(relationKind, "Hostile", StringComparison.OrdinalIgnoreCase))
        {
            return "Hostile";
        }

        return "Neutral";
    }

    private static string FormatDiplomacyRelationKind(string relationKind)
    {
        return relationKind switch
        {
            "Ally" => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationAlly"),
            "Hostile" => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationHostile"),
            _ => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationNeutral")
        };
    }

}
