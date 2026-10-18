using System;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerBankPage
{
    private readonly ClientBankPanel panel;
    private Vector2 bankScrollPosition;
    private object? owner;
    internal MultiplayerBankPage(ClientBankPanel panel) => this.panel = panel;
    internal void Tick(float now) => _ = panel.RefreshAsync(now);
    internal void Close()
    {
        panel.Close(); owner = null; bankScrollPosition = Vector2.zero;
    }
    private void EnsureCurrent()
    {
        var current = panel.Ownership;
        if (ReferenceEquals(owner, current)) return;
        owner = current; bankScrollPosition = Vector2.zero;
    }

    internal void Draw(Rect rect)
    {
        EnsureCurrent();
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 132f, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabBank"));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inner.xMax - 120f, inner.y, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Refresh")))
        {
            _ = panel.RefreshAsync(Time.realtimeSinceStartup, manual: true);
        }

        ModBankStatusResponseDto? status = panel.Read();
        Rect outRect = new(inner.x, inner.y + 40f, inner.width, inner.height - 40f);
        float contentHeight = status is null ? 360f : 560f + status.OpenDebts.Count * 90f;
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, contentHeight));
        Widgets.BeginScrollView(outRect, ref bankScrollPosition, viewRect);
        float y = 0f;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(0f, y, viewRect.width, 22f), panel.Status);
        Text.Font = GameFont.Small;
        y += 32f;

        if (status is null)
        {
            Widgets.Label(new Rect(0f, y, viewRect.width, 30f), ClashOfRimText.Key("ClashOfRim.Bank.NoStatus"));
            Widgets.EndScrollView();
            return;
        }

        Widgets.Label(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key(
            "ClashOfRim.Bank.WealthLine",
            status.ColonyWealth.Named("WEALTH"),
            status.MinLoanSilver.Named("MIN"),
            status.MaxLoanSilver.Named("MAX")));
        y += 26f;
        Widgets.Label(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key(
            "ClashOfRim.Bank.ConfigLine",
            (status.BaseAnnualInterestRate * 100f).ToString("0.#").Named("RATE"),
            status.MinDurationDays.Named("MIN"),
            status.MaxDurationDays.Named("MAX")));
        y += 26f;
        if (status.TotalOpenDebtSilver > 0)
        {
            Widgets.Label(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key(
                "ClashOfRim.Bank.DebtLine",
                status.TotalOpenDebtSilver.Named("TOTAL"),
                status.OpenDebts.Count.Named("COUNT")));
            y += 30f;
        }
        else
        {
            y += 10f;
        }

        if (status.ActiveLoan is not null)
        {
            ModBankLoanSummaryDto loan = status.ActiveLoan;
            int loanTotalDue = panel.LoanTotalDue(loan);
            Widgets.DrawMenuSection(new Rect(0f, y, viewRect.width, 122f));
            Rect box = new(8f, y + 8f, viewRect.width - 16f, 106f);
            Widgets.Label(new Rect(box.x, box.y, box.width, 24f), ClashOfRimText.Key(
                "ClashOfRim.Bank.ActiveLoanTitleWithSource",
                BankUiUtility.FormatDebtSource(loan.SourceKind).Named("SOURCE")));
            Widgets.Label(new Rect(box.x, box.y + 26f, box.width, 24f), ClashOfRimText.Key("ClashOfRim.Bank.ActiveLoanLine", loan.PrincipalSilver.Named("PRINCIPAL"), loan.InterestSilver.Named("INTEREST"), loanTotalDue.Named("TOTAL")));
            Widgets.Label(new Rect(box.x, box.y + 52f, box.width, 24f), ClashOfRimText.Key("ClashOfRim.Bank.ActiveLoanStatus", BankUiUtility.FormatLoanStatus(loan.Status).Named("STATUS")));
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(box.x, box.y + 78f, box.width - 130f, 20f), ClashOfRimText.Key("ClashOfRim.Bank.LoanDueLine", BankUiUtility.FormatLoanDue(loan).Named("DUE")));
            Text.Font = GameFont.Small;
            bool canRepayLoan = panel.CanRepay(loan.Status);
            if (Widgets.ButtonText(new Rect(box.xMax - 120f, box.yMax - 32f, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Bank.Repay"), active: canRepayLoan))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    ClashOfRimText.Key("ClashOfRim.Bank.ConfirmRepay", loanTotalDue.Named("TOTAL")),
                    panel.PrepareRepayLoan(loan, loanTotalDue)));
            }

            y += 132f;
        }

        if (status.OpenDebts.Count > 0)
        {
            Widgets.Label(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key("ClashOfRim.Bank.DebtListTitle"));
            y += 28f;
            foreach (ModBankDebtSummaryDto debt in status.OpenDebts)
            {
                Widgets.DrawMenuSection(new Rect(0f, y, viewRect.width, 82f));
                Rect box = new(8f, y + 8f, viewRect.width - 16f, 66f);
                Widgets.Label(new Rect(box.x, box.y, box.width - 130f, 24f), ClashOfRimText.Key(
                    "ClashOfRim.Bank.DebtItemLine",
                    debt.AmountSilver.Named("TOTAL"),
                    BankUiUtility.FormatDebtSource(debt.SourceKind).Named("SOURCE"),
                    BankUiUtility.FormatDebtStatus(debt.Status).Named("STATUS")));
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(box.x, box.y + 28f, box.width - 130f, 24f), ClashOfRimText.Key(
                    "ClashOfRim.Bank.DebtDetailLine",
                    BankUiUtility.FormatDebtReason(debt.Reason).Named("REASON"),
                    BankUiUtility.FormatDebtDue(debt).Named("DUE")));
                Text.Font = GameFont.Small;
                bool canRepayDebt = panel.CanRepay(debt.Status);
                if (Widgets.ButtonText(new Rect(box.xMax - 120f, box.yMax - 32f, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Bank.Repay"), active: canRepayDebt))
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        ClashOfRimText.Key("ClashOfRim.Bank.ConfirmRepayDebt", debt.AmountSilver.Named("TOTAL"), BankUiUtility.FormatDebtReason(debt.Reason).Named("REASON")),
                        panel.PrepareRepayDebt(debt)));
                }

                y += 92f;
            }
        }

        if (status.ActiveLoan is not null)
        {
            Widgets.EndScrollView();
            return;
        }

        Widgets.DrawMenuSection(new Rect(0f, y, viewRect.width, 132f));
        Rect form = new(8f, y + 8f, viewRect.width - 16f, 116f);
        var principalRange = panel.PrincipalRange;
        var durationRange = panel.DurationRange;
        int principalValue = principalRange.SliderValue(panel.PrincipalText);
        int durationValue = durationRange.SliderValue(panel.DurationText);
        Widgets.Label(new Rect(form.x, form.y, 140f, 24f), ClashOfRimText.Key("ClashOfRim.Bank.Principal"));
        panel.PrincipalText = Widgets.TextField(new Rect(form.x + 150f, form.y, 92f, 24f), panel.PrincipalText);
        int sliderPrincipal = principalRange.Max > principalRange.Min
            ? Mathf.RoundToInt(Widgets.HorizontalSlider(
                new Rect(form.x + 252f, form.y + 2f, Math.Max(80f, form.width - 380f), 24f),
                principalValue,
                principalRange.Min,
                principalRange.Max,
                roundTo: 1f))
            : principalRange.Min;
        if (sliderPrincipal != principalValue)
        {
            panel.PrincipalText = sliderPrincipal.ToString();
        }

        Widgets.Label(new Rect(form.x, form.y + 34f, 140f, 24f), ClashOfRimText.Key("ClashOfRim.Bank.Duration"));
        panel.DurationText = Widgets.TextField(new Rect(form.x + 150f, form.y + 34f, 92f, 24f), panel.DurationText);
        int sliderDuration = durationRange.Max > durationRange.Min
            ? Mathf.RoundToInt(Widgets.HorizontalSlider(
                new Rect(form.x + 252f, form.y + 36f, Math.Max(80f, form.width - 380f), 24f),
                durationValue,
                durationRange.Min,
                durationRange.Max,
                roundTo: 1f))
            : durationRange.Min;
        if (sliderDuration != durationValue)
        {
            panel.DurationText = sliderDuration.ToString();
        }

        var quote = panel.Quote();
        if (quote.Valid)
        {
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(form.x, form.y + 68f, form.width - 132f, 22f), ClashOfRimText.Key(
                "ClashOfRim.Bank.LoanPreview",
                quote.Principal.Named("PRINCIPAL"),
                quote.Interest.Named("INTEREST"),
                quote.TotalDue.Named("TOTAL")));
            Text.Font = GameFont.Small;
        }
        else if (!quote.CanBorrow)
        {
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(form.x, form.y + 68f, form.width - 132f, 22f), status.BankLoansEnabled
                ? ClashOfRimText.Key("ClashOfRim.Bank.CreateHint")
                : ClashOfRimText.Key("ClashOfRim.Bank.Disabled"));
            Text.Font = GameFont.Small;
        }

        if (Widgets.ButtonText(new Rect(form.xMax - 120f, form.yMax - 32f, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Bank.CreateLoan"), active: quote.CanBorrow))
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key("ClashOfRim.Bank.ConfirmCreate", quote.Principal.Named("PRINCIPAL"), quote.Duration.Named("DAYS")),
                panel.PrepareBorrow(quote)));
        }

        Widgets.EndScrollView();
    }

}
