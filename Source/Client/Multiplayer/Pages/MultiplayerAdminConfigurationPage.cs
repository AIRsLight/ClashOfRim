using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Protocol;
using RimWorld;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.Multiplayer.Pages.MultiplayerAdminLayout;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerAdminConfigurationPage
{
    private readonly ClientAdminConfigurationEditor model;
    private ClientAdminConfigurationDraft? owner;
    private Vector2 scroll;
    internal MultiplayerAdminConfigurationPage(ClientAdminConfigurationEditor model) => this.model = model;
    internal void Close() { model.Close(); owner = null; scroll = Vector2.zero; }
    private Rect BeginScroll(Rect rect, ClientAdminConfigurationDraft draft, float height)
    {
        if (!ReferenceEquals(owner, draft)) { owner = draft; scroll = Vector2.zero; }
        return BeginAdminScrollWithSaveFooter(rect, ref scroll, height);
    }

    internal void DrawAdminTradePanel(Rect rect, ClientAdminConfigurationDraft? draft)
    {
        if (draft is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        Rect view = BeginScroll(rect, draft, 762f + draft.FixedTradeFees.Count * 34f);
        float y = 0f;
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.TradeEnabled"), ref draft.TradeMarketplaceEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.TradeExpirationDays"), ref draft.TradeOrderExpirationDays.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MaxOpenTradeOrders"), ref draft.MaxOpenTradeOrdersPerOwner.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.TradeBaseFeeRate"), ref draft.TradeBaseFeeRate.Text);
        DrawAdminTradeFeeStrategyRow(view, ref y, draft);
        DrawAdminFixedTradeFeeRows(view, ref y, draft);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.PostageBase"), ref draft.TradePostageBaseSilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.PostagePerTile"), ref draft.TradePostageSilverPerTile.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.PostageCrossLayer"), ref draft.TradePostageCrossLayerOverheadDistanceTiles.Text);
        Widgets.EndScrollView();
        DrawAdminSaveFooter(rect, model, draft);
    }

    internal void DrawAdminBankPanel(Rect rect, ClientAdminConfigurationDraft? draft)
    {
        if (draft is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        Rect view = BeginScroll(rect, draft, 510f + draft.PenaltyStages.Count * 34f);
        float y = 0f;
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankEnabled"), ref draft.BankLoansEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankMinLoanSilver"), ref draft.BankMinLoanSilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankMaxLoanSilver"), ref draft.BankMaxLoanSilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankLoanRatio"), ref draft.BankMaxLoanWealthRatio.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankInterestRate"), ref draft.BankBaseAnnualInterestRate.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankMinDays"), ref draft.BankMinDurationDays.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankMaxDays"), ref draft.BankMaxDurationDays.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankInterestCurve"), ref draft.BankInterestDurationMultiplierCurve.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltyInterval"), ref draft.BankPenaltyIntervalDays.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltyPoints"), ref draft.BankPenaltyRaidPointsPerSilver.Text);
        DrawAdminBankOverduePenaltyRows(view, ref y, draft);
        Widgets.EndScrollView();
        DrawAdminSaveFooter(rect, model, draft);
    }

    internal void DrawAdminMercenaryPanel(Rect rect, ClientAdminConfigurationDraft? draft)
    {
        if (draft is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        Rect view = BeginScroll(rect, draft, 690f);
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionMercenaryContracts"));
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercEnabled"), ref draft.MercenariesEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercApprentice"), ref draft.MercenaryApprenticeDailySilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercSkilled"), ref draft.MercenarySkilledDailySilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercMaster"), ref draft.MercenaryMasterDailySilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercMinDays"), ref draft.MercenaryMinDurationDays.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercMaxDays"), ref draft.MercenaryMaxDurationDays.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercDurationCurve"), ref draft.MercenaryDurationMultiplierCurve.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercMaxActive"), ref draft.MaxActiveMercenariesPerColony.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercHarmfulSurgery"), ref draft.MercenaryHarmfulSurgeryFineSilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercApprenticeDeath"), ref draft.MercenaryApprenticeDeathFineSilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercSkilledDeath"), ref draft.MercenarySkilledDeathFineSilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercMasterDeath"), ref draft.MercenaryMasterDeathFineSilver.Text);
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionMercenaryGuards"));
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardEnabled"), ref draft.MercenaryGuardsEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardApprentice"), ref draft.MercenaryGuardApprenticeSilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardSkilled"), ref draft.MercenaryGuardSkilledSilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardMaster"), ref draft.MercenaryGuardMasterSilver.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardApprenticeRatio"), ref draft.MercenaryGuardApprenticePointsRatio.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardSkilledRatio"), ref draft.MercenaryGuardSkilledPointsRatio.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardMasterRatio"), ref draft.MercenaryGuardMasterPointsRatio.Text);
        Widgets.EndScrollView();
        DrawAdminSaveFooter(rect, model, draft);
    }

    internal void DrawAdminRaidPanel(Rect rect, ClientAdminConfigurationDraft? draft)
    {
        if (draft is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        const float raidAdminContentHeight = (2f * 34f) + (13f * 32f) + 16f;
        Rect view = BeginScroll(rect, draft, raidAdminContentHeight);
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionRaid"));
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.PvpEnabled"), ref draft.PvpEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidProtectionHours"), ref draft.RaidProtectionHours.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidMaxDurationMinutes"), ref draft.RaidMaxDurationMinutes.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidTimeoutGraceMinutes"), ref draft.RaidTimeoutGraceMinutes.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidMinimumDefenderWealth"), ref draft.RaidMinimumDefenderWealth.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidLossRatio"), ref draft.RaidSettlementLossRatio.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidBuildingHpLossRatio"), ref draft.RaidSettlementBuildingHitPointsLossRatio.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidMinimumHpRatio"), ref draft.RaidSettlementMinimumRemainingHitPointsRatio.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.PendingConfirmationMinutes"), ref draft.PendingConfirmationTimeoutMinutes.Text);

        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionCooldown"));
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RelationCooldownHours"), ref draft.DiplomacyRelationChangeCooldownHours.Text);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SupportCooldownMinutes"), ref draft.DiplomacySupportRequestCooldownMinutes.Text);
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.GiftsEnabled"), ref draft.GiftsEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.ForcedGiftCooldownMinutes"), ref draft.ForcedGiftDeliveryCooldownMinutes.Text);
        Widgets.EndScrollView();
        DrawAdminSaveFooter(rect, model, draft);
    }

    private void DrawAdminTradeFeeStrategyRow(Rect view, ref float y, ClientAdminConfigurationDraft draft)
    {
        Widgets.Label(new Rect(0f, y, 300f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategy"));
        if (DrawAdminSelectionButton(
                new Rect(310f, y, 180f, 26f),
                AdminTradeFeeStrategyLabel(draft.TradeFeeStrategy)))
        {
            OpenAdminTradeFeeStrategyMenu(draft);
        }

        y += 32f;
    }

    private void DrawAdminFixedTradeFeeRows(Rect view, ref float y, ClientAdminConfigurationDraft draft)
    {
        y += 6f;
        Widgets.Label(new Rect(0f, y, 300f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.FixedTradeFees"));
        if (Widgets.ButtonText(new Rect(310f, y, 120f, 26f), ClashOfRimText.Key("ClashOfRim.Admin.AddFixedTradeFee"), active: !model.Busy))
        {
            var add = model.AddFee(draft);
            Find.WindowStack.Add(new AdminThingDefSelectionDialogWindow(def => add(def.defName)));
        }

        y += 32f;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(20f, y, 300f, 20f), ClashOfRimText.Key("ClashOfRim.Admin.FixedTradeFeeThing"));
        Widgets.Label(new Rect(330f, y, 160f, 20f), ClashOfRimText.Key("ClashOfRim.Admin.FixedTradeFeeSilver"));
        Text.Font = GameFont.Small;
        y += 22f;

        for (int i = 0; i < draft.FixedTradeFees.Count; i++)
        {
            ClientAdminFixedTradeFeeInput row = draft.FixedTradeFees[i];
            string thingLabel = AdminThingLabel(row.ThingDefName);
            if (DrawAdminSelectionButton(
                    new Rect(20f, y, 300f, 26f),
                    thingLabel,
                    !model.Busy))
            {
                var select = model.SelectFeeThing(draft, row);
                Find.WindowStack.Add(new AdminThingDefSelectionDialogWindow(def => select(def.defName)));
            }

            row.SilverPerUnit = Widgets.TextField(new Rect(330f, y, 120f, 26f), row.SilverPerUnit);
            if (Widgets.ButtonText(new Rect(470f, y, 80f, 26f), ClashOfRimText.Key("ClashOfRim.Admin.Remove"), active: !model.Busy))
            {
                draft.FixedTradeFees.RemoveAt(i);
                i--;
                y += 30f;
                continue;
            }

            y += 30f;
        }

        y += 6f;
    }

    private void DrawAdminBankOverduePenaltyRows(Rect view, ref float y, ClientAdminConfigurationDraft draft)
    {
        y += 6f;
        Widgets.Label(new Rect(0f, y, 300f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.BankOverduePenaltyStages"));
        if (Widgets.ButtonText(new Rect(310f, y, 120f, 26f), ClashOfRimText.Key("ClashOfRim.Admin.AddBankPenaltyEvent"), active: !model.Busy))
        {
            var add = model.AddPenalty(draft);
            Find.WindowStack.Add(new AdminIncidentDefSelectionDialogWindow(def => add(def.defName)));
        }

        y += 32f;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(20f, y, 100f, 20f), ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltyStartCount"));
        Widgets.Label(new Rect(130f, y, 300f, 20f), ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltyEvent"));
        Widgets.Label(new Rect(440f, y, 90f, 20f), ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltySeverity"));
        Text.Font = GameFont.Small;
        y += 22f;

        for (int i = 0; i < draft.PenaltyStages.Count; i++)
        {
            ClientAdminPenaltyInput row = draft.PenaltyStages[i];
            row.TriggerPenaltyCount = Widgets.TextField(new Rect(20f, y, 100f, 26f), row.TriggerPenaltyCount);
            if (DrawAdminSelectionButton(
                    new Rect(130f, y, 300f, 26f),
                    AdminPenaltyEventLabel(row.Kind),
                    !model.Busy))
            {
                var select = model.SelectPenaltyKind(draft, row);
                Find.WindowStack.Add(new AdminIncidentDefSelectionDialogWindow(def => select(def.defName)));
            }

            row.Severity = Widgets.TextField(new Rect(440f, y, 80f, 26f), row.Severity);
            if (Widgets.ButtonText(new Rect(540f, y, 80f, 26f), ClashOfRimText.Key("ClashOfRim.Admin.Remove"), active: !model.Busy))
            {
                draft.PenaltyStages.RemoveAt(i);
                i--;
                y += 30f;
                continue;
            }

            y += 30f;
        }

        y += 6f;
    }

    private string AdminTradeFeeStrategyLabel(string strategy)
    {
        return TradeFeeStrategies.Normalize(strategy) switch
        {
            "HighestSide" => ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategyHighestSide"),
            "SumBothSides" => ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategySumBothSides"),
            _ => ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategyPublisher")
        };
    }

    private void OpenAdminTradeFeeStrategyMenu(ClientAdminConfigurationDraft draft)
    {
        var select = model.SelectFeeStrategy(draft);
        var options = new List<FloatMenuOption>
        {
            new(ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategyPublisher"), () => select("Publisher")),
            new(ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategyHighestSide"), () => select("HighestSide")),
            new(ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategySumBothSides"), () => select("SumBothSides"))
        };
        Find.WindowStack.Add(new FloatMenu(options));
    }

    private static string AdminThingLabel(string defName)
    {
        ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def is null)
        {
            return string.IsNullOrWhiteSpace(defName)
                ? ClashOfRimText.Key("ClashOfRim.Admin.SelectThing")
                : defName;
        }

        return def.LabelCap.ToString();
    }

    private static string AdminPenaltyEventLabel(string kind)
    {
        if (string.Equals(kind, "PsychicWhisper", StringComparison.OrdinalIgnoreCase))
        {
            return ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltyEventPsychicWhisper");
        }

        IncidentDef? def = DefDatabase<IncidentDef>.GetNamedSilentFail(kind);
        if (def is null)
        {
            return string.IsNullOrWhiteSpace(kind)
                ? ClashOfRimText.Key("ClashOfRim.Admin.SelectEvent")
                : kind;
        }

        return def.LabelCap.ToString();
    }
}
