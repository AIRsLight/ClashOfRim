using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerMercenaryPage
{
    private readonly ClientMercenaryPanel panel;
    private Vector2 mercenaryScrollPosition;
    private object? owner;
    internal MultiplayerMercenaryPage(ClientMercenaryPanel panel) => this.panel = panel;
    internal void Close()
    {
        panel.Close(); owner = null; mercenaryScrollPosition = Vector2.zero;
    }
    private void EnsureCurrent()
    {
        var current = panel.Ownership;
        if (ReferenceEquals(owner, current)) return;
        owner = current; mercenaryScrollPosition = Vector2.zero;
    }

    internal void Draw(Rect rect)
    {
        EnsureCurrent();
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabMercenary"));
        Text.Font = GameFont.Small;

        Rect outRect = new(inner.x, inner.y + 40f, inner.width, inner.height - 40f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, 560f));
        Widgets.BeginScrollView(outRect, ref mercenaryScrollPosition, viewRect);
        float y = 0f;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(0f, y, viewRect.width, 22f), panel.Status);
        Text.Font = GameFont.Small;
        y += 34f;

        IReadOnlyList<MercenaryProfession> professions = MercenarySkillUtility.AvailableProfessions();
        panel.ReconcileProfessions(professions.Select(profession => profession.Key));
        var selected = professions.FirstOrDefault(profession => profession.Key == panel.Skill);

        float selectorColumnWidth = Math.Min(360f, (viewRect.width - 18f) / 2f);
        string professionLabel = ClashOfRimText.Key("ClashOfRim.Mercenary.Profession");
        string tierLabel = ClashOfRimText.Key("ClashOfRim.Mercenary.Tier");
        float labelWidth = Mathf.Max(72f, Mathf.Max(Text.CalcSize(professionLabel).x, Text.CalcSize(tierLabel).x));
        Widgets.Label(new Rect(0f, y + 4f, labelWidth, 24f), professionLabel);
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(labelWidth + 6f, y, selectorColumnWidth - labelWidth - 6f, 28f),
                selected?.Label ?? panel.Skill))
        {
            List<FloatMenuOption> options = professions
                .Select(profession => new FloatMenuOption(profession.Label, panel.PrepareSelectProfession(profession.Key)))
                .ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        float tierX = selectorColumnWidth + 18f;
        Widgets.Label(new Rect(tierX, y + 4f, labelWidth, 24f), tierLabel);
        DrawMercenaryTierDropdown(new Rect(tierX + labelWidth + 6f, y, selectorColumnWidth - labelWidth - 6f, 28f));

        y += 42f;
        var range = panel.DurationRange;
        string durationLabel = MercenaryUiUtility.FormatDurationLine(panel.Days);
        float durationWidth = Mathf.Max(180f, Text.CalcSize(durationLabel).x);
        Widgets.Label(new Rect(0f, y, durationWidth, 24f), durationLabel);
        int days = range.Max > range.Min
            ? Mathf.RoundToInt(Widgets.HorizontalSlider(new Rect(durationWidth + 10f, y + 2f, 260f, 24f), panel.Days, range.Min, range.Max, roundTo: 1f))
            : range.Min;
        panel.SelectDays(days);
        _ = panel.RefreshQuotesAsync();
        y += 42f;
        var quote = panel.ReadQuote(isGuard: false);
        DrawQuote(new Rect(0f, y, viewRect.width, 76f), quote, isGuard: false, () =>
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                MercenaryUiUtility.FormatConfirmHire(selected, panel.Skill, panel.Level, panel.Days, quote.Price),
                panel.PrepareHire(isGuard: false)));
        });
        y += 88f;

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.HireHint"));
        Text.Font = GameFont.Small;

        y += 36f;
        DrawMercenaryGuardSection(new Rect(0f, y, viewRect.width, 176f));

        Widgets.EndScrollView();
    }

    private void DrawMercenaryGuardSection(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        float y = inner.y;
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inner.x, y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.GuardTitle"));
        y += 34f;

        Widgets.Label(new Rect(inner.x, y, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.GuardTier"));
        DrawMercenaryGuardTierDropdown(new Rect(inner.x + 130f, y, 220f, 28f));
        y += 42f;

        var quote = panel.ReadQuote(isGuard: true);
        DrawQuote(new Rect(inner.x, y, inner.width, 66f), quote, isGuard: true, () =>
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key("ClashOfRim.Mercenary.GuardConfirmHire",
                    MercenaryUiUtility.GuardTierLabel(panel.GuardTier).Named("TIER"), quote.Price.Named("PRICE")),
                panel.PrepareHire(isGuard: true)));
        });
        y += 78f;

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, y, inner.width, 34f), ClashOfRimText.Key("ClashOfRim.Mercenary.GuardHint"));
        Text.Font = GameFont.Small;
    }

    private void DrawMercenaryTierDropdown(Rect rect)
    {
        if (!ClashOfRimUiUtility.SelectionButton(rect, MercenarySkillUtility.TierLabel(panel.Level))) return;
        Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
        {
            new(MercenarySkillUtility.TierLabel(7), panel.PrepareSelectLevel(7)),
            new(MercenarySkillUtility.TierLabel(14), panel.PrepareSelectLevel(14)),
            new(MercenarySkillUtility.TierLabel(20), panel.PrepareSelectLevel(20))
        }));
    }
    private void DrawMercenaryGuardTierDropdown(Rect rect)
    {
        if (!ClashOfRimUiUtility.SelectionButton(rect, MercenaryUiUtility.GuardTierLabel(panel.GuardTier))) return;
        Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
        {
            new(MercenaryUiUtility.GuardTierLabel("Apprentice"), panel.PrepareSelectGuard("Apprentice")),
            new(MercenaryUiUtility.GuardTierLabel("Skilled"), panel.PrepareSelectGuard("Skilled")),
            new(MercenaryUiUtility.GuardTierLabel("Master"), panel.PrepareSelectGuard("Master"))
        }));
    }
    private static void DrawQuote(Rect rect, ClientMercenaryPanelQuote quote, bool isGuard, Action hire)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        const float buttonWidth = 140f;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - buttonWidth - 12f, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.PriceTitle"));
        Text.Font = GameFont.Tiny;
        string status = !quote.Enabled ? ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled")
            : quote.Waiting ? ClashOfRimText.Key("ClashOfRim.Mercenary.PriceLoading") : MercenaryUiUtility.FormatQuoteStatus(quote.Status);
        Widgets.Label(new Rect(inner.x, inner.y + 26f, inner.width - buttonWidth - 12f, inner.height - 26f), status);
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inner.xMax - buttonWidth, inner.yMax - 34f, buttonWidth, 30f),
            ClashOfRimText.Key(isGuard ? "ClashOfRim.Mercenary.GuardHire" : "ClashOfRim.Mercenary.Hire"), active: quote.CanHire)) hire();
    }
}
