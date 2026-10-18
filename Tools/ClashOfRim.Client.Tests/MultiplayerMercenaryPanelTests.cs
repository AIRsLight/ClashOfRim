using System;
using System.Globalization;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;

internal static class MultiplayerMercenaryPanelTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await SelectionsAndConfiguration(); await IndependentQuotes();
                foreach (bool guard in new[] { false, true }) { await QuoteCacheAndAdmission(guard); await Hire(guard); }
            }
            foreach (bool guard in new[] { false, true })
            {
                foreach (bool queued in new[] { false, true })
                foreach (string change in new[] { "selection", "return", "snapshot" }) await StaleQuote(guard, queued, change);
                foreach (string outcome in new[] { "rejected", "transport", "missing", "exception", "cancelled" }) await Failure(guard, outcome);
                foreach (string change in new[] { "close", "user", "colony", "auth", "server", "game", "login", "generation", "reset", "selection", "snapshot" })
                    await OldActions(guard, change);
                foreach (string change in new[] { "close", "user", "colony", "auth", "server", "game", "login", "generation" })
                    await ReplacedRequest(guard, change);
            }
            await PvpToggle(); await NewerBankConfiguration();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static async Task SelectionsAndConfiguration()
    {
        using var f = new Fixture();
        Check(f.Panel.Skill == "Shooting" && f.Panel.Level == 7 && f.Panel.Days == 7 && f.Panel.GuardTier == "Apprentice", "Default selections changed.");
        f.Panel.PrepareSelectProfession("I")(); f.Panel.ReconcileProfessions(new[] { "i", "I", "İ" });
        Check(f.Panel.Skill == "I", "Profession matching used culture/case instead of identity.");
        f.Panel.ReconcileProfessions(new[] { "i", "İ" }); Check(f.Panel.Skill == "i", "Missing profession did not fall back to the first available one.");
        f.Panel.ReconcileProfessions(Array.Empty<string>()); await f.Panel.RefreshQuotesAsync();
        Check(f.Quotes.Calls == 0 && !f.Panel.ReadQuote(false).CanHire, "Empty profession catalog issued an actionable quote.");
        f.Panel.ReconcileProfessions(new[] { "Mining" });
        f.EditBank(status => status.MercenaryMaxDurationDays = 0);
        await f.Panel.RefreshQuotesAsync(); Check(f.Quotes.Calls == 0 && f.Panel.ReadQuote(false).Waiting, "Missing duration configuration dispatched a worker quote.");
        f.EditBank(status => { status.MercenaryMinDurationDays = 3; status.MercenaryMaxDurationDays = 5; });
        Check(f.Panel.DurationRange == new PanelIntegerRange(3, 5) && f.Panel.Days == 5, "Config update did not clamp the selected duration.");
        f.Panel.SelectDays(1); Check(f.Panel.Days == 3, "Duration below minimum was retained.");
        f.Panel.SelectDays(4); await f.Refresh();
        Check(f.Quotes.Arguments == "Mining:7:4" && f.Panel.ReadQuote(false).CanHire, "Quote used the previous frame's slider duration.");
        f.Panel.PrepareSelectGuard("sKiLlEd")(); Check(f.Panel.GuardTier == "Apprentice", "Unknown guard tier did not retain UI normalization.");
        f.Panel.PrepareSelectGuard("Skilled")(); Check(f.Panel.GuardTier == "Skilled", "Known guard tier was lost.");
        f.EditBank(status => status.MercenariesEnabled = false);
        Check(!f.Panel.ReadQuote(false).CanHire, "Latest shared bank disable was hidden by an older quote.");
        f.Panel.Close();
        Check(f.Panel.Skill == "Shooting" && f.Panel.Level == 7 && f.Panel.GuardTier == "Apprentice", "Closing retained user selections.");
    }

    private static async Task QuoteCacheAndAdmission(bool guard)
    {
        using var f = new Fixture(guard);
        await f.Refresh(); var quote = f.Panel.ReadQuote(guard);
        Check(quote is { Waiting: false, Enabled: true, CanHire: true, Price: 350 } && quote.Status!.StartsWith("PriceLine", StringComparison.Ordinal), "Quote did not publish price/status.");
        await f.Refresh(); Check(f.Quotes.Calls == 1, "Unchanged selection fetched every frame.");
        Check(f.Hiring.Sync.TryBeginManual(out var manual), "Cannot occupy synchronization.");
        Check(!f.Panel.ReadQuote(guard).CanHire, "Manual synchronization allowed hire."); manual!.Dispose();
        Check(f.Hiring.Sync.TryAcquireSnapshot(out var upload), "Cannot occupy upload.");
        Check(!f.Panel.ReadQuote(guard).CanHire, "Snapshot upload allowed hire."); upload!.Dispose();
        var pending = f.Hiring.State.Requests.Begin()!;
        Check(!f.Panel.ReadQuote(guard).CanHire, "Active hiring did not disable its page."); f.Hiring.State.Requests.End(pending);
        Check(f.Panel.ReadQuote(guard).CanHire && f.Panel.Status == "idle", "Temporary contention destroyed a valid quote.");
        f.Hiring.Ports.Snapshot = "advanced";
        Check(!f.Panel.ReadQuote(guard).CanHire, "Old snapshot quote remained actionable.");
        await f.Refresh(); Check(f.Quotes.Calls == 2 && f.Panel.ReadQuote(guard).CanHire, "Snapshot advancement did not obtain a current quote.");
    }

    private static async Task IndependentQuotes()
    {
        using var f = new Fixture(); f.Pvp = true;
        await f.Refresh();
        Check(f.Quotes.Calls == 2 && f.Panel.ReadQuote(false).CanHire && f.Panel.ReadQuote(true).CanHire, "Worker and guard quotes overwrote each other.");
        f.Panel.PrepareSelectLevel(14)(); await f.Refresh();
        Check(f.Quotes.Calls == 3 && f.Panel.ReadQuote(true).CanHire, "Worker selection invalidated the unrelated guard quote.");
        f.Panel.PrepareSelectLevel(14)(); await f.Refresh(); Check(f.Quotes.Calls == 3, "Unchanged tier restarted its quote.");
        f.Panel.PrepareSelectProfession(f.Panel.Skill)(); await f.Refresh();
        Check(f.Quotes.Calls == 4, "Reselecting a profession lost the existing manual retry path.");
    }

    private static async Task StaleQuote(bool guard, bool queued, string change)
    {
        using var f = new Fixture(guard); f.Quotes.Block = !queued;
        Task old = f.Panel.RefreshQuotesAsync();
        Action? deliver = queued ? await f.Hiring.Ports.Next() : null;
        if (!queued) await f.Quotes.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "snapshot")
        {
            f.Hiring.Ports.Snapshot = "advanced";
            f.EditBank(status => status.ColonyWealth = 999);
        }
        else
        {
            f.SelectOther();
            if (change == "return") f.SelectOriginal();
        }
        await f.Panel.RefreshQuotesAsync();
        Check(f.Quotes.Calls == 1 && !f.Panel.ReadQuote(guard).CanHire, "Changed selection launched duplicate work or retained an actionable old quote.");
        if (deliver is not null) { deliver(); await old; }
        else { f.Quotes.Release.TrySetResult(); await f.Hiring.Ports.PumpUntil(old); }
        Check(!f.Panel.ReadQuote(guard).CanHire && f.Panel.ReadQuote(guard).Price == 0 && !f.Panel.ReadQuote(guard).Waiting,
            $"Obsolete quote was displayed for {change}, queued={queued}, guard={guard}.");
        if (change == "snapshot") Check(f.Hiring.Bank.View!.ColonyWealth == 999, "Old quote replaced the newer bank view.");
        f.Quotes.Block = false; f.Quotes.Price = 500;
        await f.Refresh(); Check(f.Quotes.Calls == 2 && f.Panel.ReadQuote(guard) is { CanHire: true, Price: 500 }, "Discarded quote stranded the replacement selection.");
    }

    private static async Task Failure(bool guard, string outcome)
    {
        using var f = new Fixture(guard); f.Quotes.Outcome = outcome;
        await f.Refresh(); Check(!f.Panel.ReadQuote(guard).CanHire && !f.Panel.ReadQuote(guard).Waiting && !string.IsNullOrEmpty(f.Panel.ReadQuote(guard).Status), "Quote failure stayed pending or allowed hire: " + outcome);
        await f.Refresh(); Check(f.Quotes.Calls == 1, "Failed quote generated a per-frame retry storm.");
        f.SelectOther(); f.Quotes.Outcome = "accepted"; await f.Refresh();
        Check(f.Quotes.Calls == 2 && f.Panel.ReadQuote(guard).CanHire, "A later selection could not recover from quote failure.");
    }

    private static async Task Hire(bool guard)
    {
        using var f = new Fixture(guard); f.SelectOther();
        if (!guard) { f.Panel.SelectDays(9); f.Panel.PrepareSelectProfession("Mining")(); }
        await f.Refresh(); Action confirm = f.Panel.PrepareHire(guard);
        confirm(); await f.Hiring.Ports.PumpUntil(f.Hiring.Done.Task);
        var selection = f.Hiring.Command!.Selection;
        Check(f.Hiring.Applied == 1 && f.Hiring.Submissions == 1 && f.Hiring.Completed == 1 && f.Hiring.Quotes == 1, "Page bypassed or duplicated the existing quote/debit/payment flow.");
        Check(guard ? selection.GuardTier == "Master" : selection.Skill == "Mining" && selection.Level == 14 && selection.Days == 9,
            "Hire confirmation lost its chosen profession, tier or duration.");
    }

    private static async Task OldActions(bool guard, string change)
    {
        using var f = new Fixture(guard); await f.Refresh();
        Action confirm = f.Panel.PrepareHire(guard);
        Action menu = guard ? f.Panel.PrepareSelectGuard("Master") : f.Panel.PrepareSelectLevel(20);
        if (change == "close") f.Panel.Close();
        else if (change == "selection") { f.SelectOther(); f.SelectOriginal(); }
        else f.Hiring.Change(change);
        confirm(); await f.Hiring.Ports.PumpUntil(Task.CompletedTask);
        Check(f.Hiring.Quotes == 0 && f.Hiring.Applied == 0 && f.Hiring.Submissions == 0, $"Old hire crossed {change}, guard={guard}.");
        if (change is not ("reset" or "selection" or "snapshot"))
        {
            menu(); Check(f.Panel.Level == 7 && f.Panel.GuardTier == "Apprentice", "Old menu changed the replacement page: " + change);
        }
    }

    private static async Task ReplacedRequest(bool guard, string change)
    {
        using var f = new Fixture(guard); f.Quotes.Block = true;
        Task old = f.Panel.RefreshQuotesAsync(); await f.Quotes.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "close") f.Panel.Close(); else f.Hiring.Change(change);
        f.Hiring.Bank.View = f.Quotes.ReplyBank;
        f.Reconcile(); f.Quotes.Block = false;
        await f.Refresh(); var current = f.Panel.ReadQuote(guard);
        f.Quotes.Price = 999; f.Quotes.Release.TrySetResult(); await f.Hiring.Ports.PumpUntil(old);
        Check(f.Panel.ReadQuote(guard) == current && current.Price == 350 && current.CanHire, "Old callback changed replacement quote after " + change);
    }

    private static async Task NewerBankConfiguration()
    {
        using var f = new Fixture(); f.Quotes.Block = true;
        Task old = f.Panel.RefreshQuotesAsync(); await f.Quotes.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.EditBank(status => { status.MercenaryMaxDurationDays = 0; status.ColonyWealth = 999; });
        f.Quotes.Release.TrySetResult(); await f.Hiring.Ports.PumpUntil(old);
        Check(f.Hiring.Bank.View!.ColonyWealth == 999 && f.Hiring.Bank.View.MercenaryMaxDurationDays == 0
            && f.Panel.ReadQuote(false) is { CanHire: false, Waiting: true }, "Late quote overwrote newer configuration on the same snapshot.");
        f.EditBank(status => status.MercenaryMaxDurationDays = 30); f.Quotes.Block = false;
        await f.Refresh(); Check(f.Panel.ReadQuote(false).CanHire, "Restored configuration did not release the discarded quote.");
    }

    private static async Task PvpToggle()
    {
        using var f = new Fixture(true); await f.Refresh();
        Action confirm = f.Panel.PrepareHire(true); f.Pvp = false;
        Check(f.Panel.ReadQuote(true) is { Enabled: false, CanHire: false, Waiting: false }, "PVP disable retained guard state.");
        confirm(); Check(f.Hiring.Quotes == 0, "Old guard confirmation bypassed PVP disable.");
        f.Pvp = true; await f.Refresh();
        Check(f.Quotes.Calls == 2 && f.Panel.ReadQuote(true).CanHire, "Reenabling PVP stayed stuck on the old completed request key.");
        confirm(); Check(f.Hiring.Quotes == 0, "Reenabling PVP revived an old confirmation.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ClientMercenaryHiringTests.Fixture Hiring;
        internal readonly ClientMercenaryQuotesTests.Fixture Quotes;
        internal readonly ClientMercenaryPanel Panel;
        private readonly bool guard;
        internal bool Pvp;
        internal Fixture(bool guard = false)
        {
            this.guard = guard; Pvp = guard; Hiring = new(guard);
            Quotes = new(Hiring.Ports, Hiring.Session, Hiring.Bank);
            Quotes.ReplyBank.MercenaryMinDurationDays = 1; Quotes.ReplyBank.MercenaryMaxDurationDays = 30; Quotes.ReplyBank.MercenariesEnabled = true;
            Hiring.Bank.View = Quotes.ReplyBank;
            Panel = new(Hiring.Session, Hiring.Ports, Hiring.State, Quotes.Quotes, Hiring.Hiring, Hiring.Sync, () => Hiring.Bank.View, () => Pvp);
            Reconcile();
        }
        internal void Reconcile() => Panel.ReconcileProfessions(guard ? Array.Empty<string>() : new[] { "Shooting", "Mining" });
        internal void EditBank(Action<ModBankStatusResponseDto> edit) { var view = Hiring.Bank.View!; edit(view); Hiring.Bank.View = view; }
        internal Task Refresh() => Hiring.Ports.PumpUntil(Panel.RefreshQuotesAsync());
        internal void SelectOther() { if (guard) Panel.PrepareSelectGuard("Master")(); else Panel.PrepareSelectLevel(14)(); }
        internal void SelectOriginal() { if (guard) Panel.PrepareSelectGuard("Apprentice")(); else Panel.PrepareSelectLevel(7)(); }
        public void Dispose() => Hiring.Dispose();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
