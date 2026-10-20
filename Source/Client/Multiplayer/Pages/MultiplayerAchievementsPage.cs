using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Achievements;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Achievements;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.Multiplayer.Players.MultiplayerPlayerText;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerAchievementsPage
{
    private readonly ClientAchievementPanel panel;
    private Vector2 achievementsScrollPosition;
    private object? owner;
    private ClientAchievementTarget? target;
    private static string UnknownPlayerName => ClashOfRimText.Key("ClashOfRim.UnknownPlayer");

    internal MultiplayerAchievementsPage(ClientAchievementPanel panel) => this.panel = panel;
    internal void Tick(float now) => _ = panel.RefreshAsync(now);
    internal void RequestRefresh() => panel.RequestRefresh();
    internal void Close()
    {
        panel.Close(); owner = null; target = null; achievementsScrollPosition = Vector2.zero;
    }
    private void EnsureCurrent()
    {
        var current = panel.Ownership;
        var selected = panel.Read().Target;
        if (ReferenceEquals(owner, current) && target == selected) return;
        owner = current; target = selected; achievementsScrollPosition = Vector2.zero;
    }
    private string TargetName(IReadOnlyList<ModPlayerSummaryDto> players) =>
        panel.TargetDisplayName(players, UnknownPlayerName, ClashOfRimText.Key("ClashOfRim.Achievement.SelectPlayer"));

    internal bool Draw(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        EnsureCurrent();
        IReadOnlyList<ModPlayerSummaryDto> selectablePlayers = panel.SelectablePlayers(UnknownPlayerName);

        Text.Font = GameFont.Medium;
        string title = ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAchievements");
        float titleWidth = Text.CalcSize(title).x + 8f;
        Widgets.Label(new Rect(inner.x, inner.y, titleWidth, 30f), title);
        Text.Font = GameFont.Small;
        Rect selectorRect = new(inner.x + titleWidth + 8f, inner.y, Mathf.Min(260f, inner.width - titleWidth - 144f), 30f);
        if (ClashOfRimUiUtility.SelectionButton(
                selectorRect,
                TargetName(selectablePlayers),
                tooltip: ClashOfRimText.Key("ClashOfRim.Achievement.SelectPlayer")))
        {
            List<FloatMenuOption> options = selectablePlayers
                .Select(player =>
                {
                    var select = panel.PrepareSelection(player);
                    return new FloatMenuOption(PlayerDisplayName(player), () => _ = select(Time.realtimeSinceStartup));
                })
                .ToList();
            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.Multiplayer.NoPlayers"), null));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        if (Widgets.ButtonText(new Rect(inner.xMax - 120f, inner.y, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Refresh"), active: !panel.Busy))
        {
            _ = panel.RefreshAsync(Time.realtimeSinceStartup, force: true);
        }

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.y + 32f, inner.width, 20f), panel.Status);
        Text.Font = GameFont.Small;

        var snapshot = panel.Read();
        IReadOnlyList<ModAchievementSummaryDto> achievements = snapshot.Achievements;
        Rect outRect = new(inner.x, inner.y + 60f, inner.width, inner.height - 60f);
        const float tileWidth = 220f;
        bool canCreateTrophy = snapshot.IsSelf;
        const float tileHeight = 116f;
        const float gap = 10f;
        int columns = Math.Max(1, Mathf.FloorToInt((outRect.width - 16f + gap) / (tileWidth + gap)));
        int rows = Math.Max(1, Mathf.CeilToInt(achievements.Count / (float)columns));
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, rows * (tileHeight + gap)));
        bool close = false;
        Widgets.BeginScrollView(outRect, ref achievementsScrollPosition, viewRect);
        if (achievements.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 28f), ClashOfRimText.Key("ClashOfRim.Achievement.OwnEmpty"));
        }
        else
        {
            for (int index = 0; index < achievements.Count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                Rect tile = new(
                    column * (tileWidth + gap),
                    row * (tileHeight + gap),
                    tileWidth,
                    tileHeight);
                close |= DrawOwnAchievementTile(tile, achievements[index], selectablePlayers, canCreateTrophy);
            }
        }

        Widgets.EndScrollView();
        Text.Font = GameFont.Small;
        return close;
    }

    private bool DrawOwnAchievementTile(
        Rect rect,
        ModAchievementSummaryDto achievement,
        IReadOnlyList<ModPlayerSummaryDto> selectablePlayers,
        bool canCreateTrophy)
    {
        Widgets.DrawMenuSection(rect);
        DrawAchievementColorBorder(rect, achievement.Color, 2);
        Widgets.DrawHighlightIfMouseover(rect);
        Rect iconRect = new(rect.x + 10f, rect.y + 16f, 48f, 48f);
        Widgets.DrawBoxSolid(iconRect, new Color(0.16f, 0.18f, 0.19f, 0.85f));
        GUI.DrawTexture(iconRect.ContractedBy(6f), AchievementIcon(achievement.IconId), ScaleMode.ScaleToFit);

        Text.Font = GameFont.Small;
        Rect textRect = new(rect.x + 68f, rect.y + 12f, rect.width - 78f, 24f);
        TradeUiUtility.DrawTruncatedLabel(textRect, AchievementText.Label(achievement.LabelKey, achievement.AchievementId));
        Text.Font = GameFont.Tiny;
        Rect descriptionRect = new(rect.x + 68f, rect.y + 38f, rect.width - 78f, 22f);
        TradeUiUtility.DrawTruncatedLabel(descriptionRect, AchievementText.Description(achievement.DescriptionKey));
        Text.Anchor = TextAnchor.UpperRight;
        Widgets.Label(new Rect(rect.xMax - 76f, rect.y + 62f, 66f, 20f), AchievementText.Value(achievement.Value));
        Text.Anchor = TextAnchor.UpperLeft;
        if (canCreateTrophy)
        {
            Rect buttonRect = new(rect.x + 68f, rect.yMax - 30f, rect.width - 78f, 24f);
            if (Widgets.ButtonText(buttonRect, ClashOfRimText.Key("ClashOfRim.Achievement.GetTrophyBlueprint")))
            {
                bool created = panel.PrepareTrophy(achievement.AchievementId, TargetName(selectablePlayers))();
                Text.Font = GameFont.Small;
                return created;
            }
        }

        Text.Font = GameFont.Small;
        return false;
    }

    private static Texture2D AchievementIcon(string? iconId)
    {
        return ContentFinder<Texture2D>.Get("UI/ClashOfRim/AchievementDefault", reportFailure: false)
            ?? BaseContent.WhiteTex;
    }

    private static void DrawAchievementColorBorder(Rect rect, string? color, int thickness)
    {
        Color previousColor = GUI.color;
        GUI.color = AchievementBorderColor(color);
        Widgets.DrawBox(rect, thickness);
        GUI.color = previousColor;
    }

    private static Color AchievementBorderColor(string? color)
    {
        if (string.Equals(color, "Blue", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(0.36f, 0.68f, 1f, 0.95f);
        }

        if (string.Equals(color, "Purple", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(0.70f, 0.48f, 1f, 0.95f);
        }

        if (string.Equals(color, "Red", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(1f, 0.35f, 0.30f, 0.95f);
        }

        string? trimmed = color?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed)
            && ColorUtility.TryParseHtmlString(trimmed, out Color parsed))
        {
            if (parsed.a <= 0f)
            {
                parsed.a = 0.95f;
            }

            return parsed;
        }

        return new Color(0.42f, 0.82f, 0.42f, 0.95f);
    }

}
