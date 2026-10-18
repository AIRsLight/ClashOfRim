using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal sealed class ClientMercenaryPanel
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientMercenaryState state;
    private readonly ClientMercenaryQuotes quotes;
    private readonly ClientMercenaryHiring hiring;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly Func<ModBankStatusResponseDto?> readBank;
    private readonly Func<bool> pvpEnabled;
    private readonly ClientGameRequestTracker lifetime;
    private Slot worker = Worker();
    private Slot guard = Guard();
    private bool hasProfession;

    internal ClientMercenaryPanel(ClientSessionLifecycle session, IClientSnapshotGame game, ClientMercenaryState state,
        ClientMercenaryQuotes quotes, ClientMercenaryHiring hiring, ClientSynchronizationCoordinator synchronization,
        Func<ModBankStatusResponseDto?> readBank, Func<bool> pvpEnabled)
    {
        this.session = session; this.state = state; this.quotes = quotes; this.hiring = hiring;
        this.synchronization = synchronization; this.readBank = readBank; this.pvpEnabled = pvpEnabled;
        lifetime = new(session, game, () => { worker = Worker(); guard = Guard(); hasProfession = false; });
    }
    private static Slot Worker() => new(new("Shooting", 7, 7));
    private static Slot Guard() => new(new("", 0, 0, "Apprentice"));
    internal object Ownership => lifetime.Capture();
    internal string Status => state.Message;
    internal string Skill => Read(() => worker.Selection.Skill);
    internal int Level => Read(() => worker.Selection.Level);
    internal int Days => Read(() => worker.Selection.Days);
    internal string GuardTier => Read(() => guard.Selection.GuardTier!);
    internal PanelIntegerRange DurationRange => Read(Range);
    internal void Close() => lifetime.Reset();

    private PanelIntegerRange Range()
    {
        var status = readBank();
        return status is null ? new(1, 180) : PanelIntegerRange.Normalize(status.MercenaryMinDurationDays, status.MercenaryMaxDurationDays, 1, 180);
    }
    private bool HasDurationRange => readBank()?.MercenaryMaxDurationDays > 0;
    private T Read<T>(Func<T> read) => lifetime.Read(() =>
    {
        var range = Range();
        int days = Math.Clamp(worker.Selection.Days, range.Min, range.Max);
        if (days != worker.Selection.Days) Change(worker, worker.Selection with { Days = days });
        if (!pvpEnabled()) guard.Clear();
        return read();
    });
    internal void ReconcileProfessions(IEnumerable<string> available) => Read(() =>
    {
        string? first = null;
        bool found = false;
        foreach (string key in available)
        {
            first ??= key;
            if (key == worker.Selection.Skill) { found = true; break; }
        }
        hasProfession = first is not null;
        if (!found && first is not null) Change(worker, worker.Selection with { Skill = first });
        if (!hasProfession) worker.Clear();
        return true;
    });
    internal void SelectDays(int days) => Read(() =>
    {
        var range = Range();
        Change(worker, worker.Selection with { Days = Math.Clamp(days, range.Min, range.Max) });
        return true;
    });
    internal Action PrepareSelectProfession(string skill) => BindMenu(() => Change(worker, worker.Selection with { Skill = skill }, force: true));
    internal Action PrepareSelectLevel(int level) => BindMenu(() => Change(worker, worker.Selection with { Level = level }));
    internal Action PrepareSelectGuard(string tier) => BindMenu(() => Change(guard, guard.Selection with { GuardTier = ClientMercenarySelection.NormalizeGuardTier(tier) }));
    private Action BindMenu(Action action)
    {
        var owner = lifetime.Capture();
        return () => lifetime.TryApply(owner, action);
    }
    private static void Change(Slot slot, ClientMercenarySelection selection, bool force = false)
    {
        if (!force && slot.Selection == selection) return;
        slot.Selection = selection;
        slot.Last = null; slot.Quote = null;
        // Keep one transport request per kind. Its callback releases pending
        // work but cannot publish for a replacement selection, even A -> B -> A.
    }

    internal Task RefreshQuotesAsync() => Read(() => Task.WhenAll(
        hasProfession && HasDurationRange ? Ensure(worker) : Task.CompletedTask,
        pvpEnabled() ? Ensure(guard) : Task.CompletedTask));
    private Task Ensure(Slot slot)
    {
        if (slot.Pending is not null || Matches(slot.Last, slot)) return Task.CompletedTask;
        var owner = lifetime.Capture();
        var request = new Request(slot.Selection, session.ReadContext().CurrentSnapshotId);
        slot.Pending = request; slot.Quote = null;
        var selection = request.Selection;
        void Complete(string message, bool present, bool accepted, int price, bool enabled) => lifetime.TryApply(owner, () =>
        {
            if (!ReferenceEquals(slot.Pending, request)) return;
            slot.Pending = null;
            if (!Matches(request, slot) || (selection.IsGuard ? !pvpEnabled() : !hasProfession || !HasDurationRange)) return;
            slot.Last = request;
            slot.Quote = new(message, present && accepted, price, enabled);
        });
        // The business API still echoes a caller key for legacy consumers. This
        // page uses typed request identity and does not serialize UI state into it.
        return selection.IsGuard
            ? quotes.QuoteGuardAsync("", selection.GuardTier!, (_, message, response) =>
                Complete(message, response is not null, response?.Result?.Accepted != false, response?.PriceSilver ?? 0, response?.BankStatus?.MercenariesEnabled ?? true))
            : quotes.QuoteAsync("", selection.Skill, selection.Level, selection.Days, (_, message, response) =>
                Complete(message, response is not null, response?.Result?.Accepted != false, response?.PriceSilver ?? 0, response?.BankStatus?.MercenariesEnabled ?? true));
    }
    private bool Matches(Request? request, Slot slot) => request is not null && ReferenceEquals(request.Selection, slot.Selection)
        && request.Snapshot == session.ReadContext().CurrentSnapshotId;
    internal ClientMercenaryPanelQuote ReadQuote(bool isGuard) => Read(() => Describe(isGuard ? guard : worker));
    private ClientMercenaryPanelQuote Describe(Slot slot)
    {
        bool isGuard = slot.Selection.IsGuard;
        bool enabled = !isGuard || pvpEnabled();
        bool available = isGuard || (hasProfession && HasDurationRange);
        var quote = Matches(slot.Last, slot) ? slot.Quote : null;
        bool canHire = enabled && available && quote is { Accepted: true, Enabled: true } && slot.Pending is null
            && readBank()?.MercenariesEnabled != false && !state.Busy && !synchronization.ManualInProgress && !synchronization.SnapshotInProgress;
        return new(slot.Pending is not null || (!isGuard && !HasDurationRange), enabled, quote?.Message, quote?.Price ?? 0, canHire);
    }
    internal Action PrepareHire(bool isGuard)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var owner = lifetime.Capture();
        var slot = isGuard ? guard : worker;
        var selection = slot.Selection;
        var quote = slot.Quote;
        var command = isGuard ? hiring.BindGuard(selection.GuardTier!) : hiring.BindHire(selection.Skill, selection.Level, selection.Days);
        return () => lifetime.TryApply(owner, () =>
        {
            if (ReferenceEquals(slot.Selection, selection) && ReferenceEquals(slot.Quote, quote) && Describe(slot).CanHire) command();
        });
    }

    private sealed record Request(ClientMercenarySelection Selection, string? Snapshot);
    private sealed record Quote(string Message, bool Accepted, int Price, bool Enabled);
    private sealed class Slot
    {
        internal ClientMercenarySelection Selection;
        internal Request? Pending, Last;
        internal Quote? Quote;
        internal Slot(ClientMercenarySelection selection) => Selection = selection;
        internal void Clear() { Pending = Last = null; Quote = null; }
    }
}

internal sealed record ClientMercenaryPanelQuote(bool Waiting, bool Enabled, string? Status, int Price, bool CanHire);
