using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientMercenaryQuotesTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool guard in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (string outcome in new[] { "accepted", "rejected", "transport", "missing", "exception", "cancelled" })
                    await Outcome(guard, outcome);
            }
            foreach (bool guard in new[] { false, true })
            foreach (bool queued in new[] { false, true })
            {
                foreach (string change in new[] { "user", "colony", "server", "auth", "game", "login", "generation" })
                    await Obsolete(guard, queued, change);
                await AdvancedSnapshot(guard, queued);
            }
            await Concurrent();
            await ReentrantCallback();
            await CallbackException();
            var empty = new Fixture();
            await empty.Quotes.QuoteAsync("unused", "Mining", 7, 7, null);
            await empty.Quotes.QuoteGuardAsync("unused", "Master", null);
            Check(empty.Calls == 0 && empty.TickReads == 0 && empty.Ports.Queued == 0, "Null callbacks started work.");
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }

    private static async Task Outcome(bool guard, string outcome)
    {
        var f = new Fixture { Outcome = outcome };
        var work = f.Start(guard);
        (await f.Ports.Next())(); await work;
        Check(f.Calls == 1 && f.Callbacks == 1 && f.LastKey == "selection" && f.TickReads == 1, "Quote did not finish its own callback.");
        string prefix = outcome switch { "accepted" => "PriceLine", "rejected" => "StatusQuoteRejected",
            "exception" or "cancelled" => "StatusQuoteException", _ => "StatusQuoteFailed" };
        Check(f.LastMessage!.StartsWith(prefix, StringComparison.Ordinal), "Wrong quote status: " + outcome);
        bool returnedQuote = outcome is "accepted" or "rejected";
        Check(f.HasQuote == returnedQuote && f.Bank.View!.ColonyWealth == (returnedQuote ? 123 : 9), "Quote or bank publication changed on failure.");
        Check(f.Diagnostics == (outcome is "exception" or "cancelled" ? 1 : 0), "Quote diagnosed the wrong failure.");
        Check(f.Captured!.UserId == "alice" && f.Captured.CurrentSnapshotId == "base" && f.Captured.AuthToken == "auth"
            && f.Ticks == 12345 && f.Arguments == (guard ? "sKiLlEd" : "Mining:14:9"), "Quote lost captured identity or requested terms.");
        Check(f.Ports.Captures == 0 && f.Ports.Published == 0 && f.Ports.GamePublished == 0, "Read-only quote changed the snapshot.");
        if (returnedQuote) Check(f.Bank.View!.OpenDebts![0].AmountSilver == 12, "Callback mutated the bank's copied view.");
    }

    private static async Task Obsolete(bool guard, bool queued, string change)
    {
        var f = new Fixture { Block = !queued };
        var old = f.Start(guard);
        Action? pending = queued ? await f.Ports.Next() : null;
        if (!queued) await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (change)
        {
            case "user": f.Ports.User = "bob"; break;
            case "colony": f.Ports.Colony = "new-colony"; break;
            case "server": f.Ports.Server = "http://new-server"; break;
            case "auth": f.Ports.Auth = "new-auth"; break;
            case "game": f.Ports.GameOwner = new(); break;
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "new-session" }); break;
            default: ClientSessionWork.Reset(); break;
        }
        f.Bank.View = new() { ColonyWealth = 999 };
        if (pending is not null) pending(); else f.Release.TrySetResult();
        await old.WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Callbacks == 0 && f.Bank.View!.ColonyWealth == 999 && f.Diagnostics == 0,
            "Old quote published across " + change + (queued ? " after queue" : " during transport"));
        // A stale completion cannot poison later work, even after identical-credential login.
        f.Block = false; var current = f.Start(guard, "new-selection");
        (await f.Ports.Next())(); await current;
        Check(f.Callbacks == 1 && f.LastKey == "new-selection" && f.Bank.View!.ColonyWealth == 123, "New quote was blocked by stale work.");
    }

    private static async Task AdvancedSnapshot(bool guard, bool queued)
    {
        var f = new Fixture { Block = !queued };
        var work = f.Start(guard);
        Action? pending = queued ? await f.Ports.Next() : null;
        if (!queued) await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Ports.Snapshot = "advanced"; f.Bank.View = new() { ColonyWealth = 999 };
        if (pending is null) { f.Release.TrySetResult(); pending = await f.Ports.Next(); }
        pending(); await work;
        Check(f.Callbacks == 1 && f.LastKey == "selection" && f.HasQuote && f.Bank.View!.ColonyWealth == 999,
            "Advancing the snapshot stranded the UI request or replaced the newer bank state.");
    }

    private static async Task Concurrent()
    {
        var f = new Fixture(); var keys = new List<string>();
        var normal = f.Quotes.QuoteAsync("normal", "Mining", 14, 9, (key, _, _) => keys.Add(key));
        var guard = f.Quotes.QuoteGuardAsync("guard", "sKiLlEd", (key, _, _) => keys.Add(key));
        var first = await f.Ports.Next(); var second = await f.Ports.Next();
        second(); first(); await Task.WhenAll(normal, guard);
        Check(keys.OrderBy(x => x).SequenceEqual(new[] { "guard", "normal" }), "Independent quote selections overwrote each other's callbacks.");
    }

    private static async Task ReentrantCallback()
    {
        var f = new Fixture(); Task? next = null;
        var first = f.Quotes.QuoteAsync("first", "Mining", 14, 9, (_, _, _) => next = f.Start(true, "next"));
        (await f.Ports.Next())(); await first;
        (await f.Ports.Next())(); await next!;
        Check(f.LastKey == "next" && f.Calls == 2, "Completed quote interfered with a callback's next quote.");
    }

    private static async Task CallbackException()
    {
        var f = new Fixture(); int invoked = 0;
        var work = f.Quotes.QuoteAsync("throw", "Mining", 14, 9, (_, _, _) => { invoked++; throw new InvalidOperationException("caller failure"); });
        bool gameThrew = false, taskThrew = false;
        try { (await f.Ports.Next())(); } catch (InvalidOperationException) { gameThrew = true; }
        try { await work; } catch (InvalidOperationException) { taskThrew = true; }
        Check(gameThrew && taskThrew && invoked == 1 && f.Ports.Queued == 1 && f.Diagnostics == 0, "A callback exception caused a second delivery.");
    }

    internal sealed class Fixture : IClientMercenaryQuoteTransport, IClientMercenaryPresentation, IClientBankPresentation
    {
        internal readonly EventModuleTestPorts Ports;
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientBankState Bank;
        internal readonly ClientMercenaryQuotes Quotes;
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Block, HasQuote;
        internal string Outcome = "accepted";
        internal int Calls, Callbacks, TickReads, Diagnostics;
        internal long Ticks;
        internal string? LastKey, LastMessage, Arguments;
        internal ClashOfRimClientNetworkContext? Captured;
        internal int Price = 350;
        internal ModBankStatusResponseDto ReplyBank = new() { ColonyWealth = 123, OpenDebts = new() { new() { DebtId = "fine", AmountSilver = 12 } } };
        internal Fixture(EventModuleTestPorts? ports = null, ClientSessionLifecycle? session = null, ClientBankState? bank = null)
        {
            if (session is null) ClientSessionWork.Reset();
            Ports = ports ?? new(); Session = session ?? new(Ports, Ports, new ClientSessionConnection(Ports));
            Bank = bank ?? new(Session, Ports, this);
            if (bank is null) Bank.View = new() { ColonyWealth = 9 };
            Quotes = new(Session, Ports, Bank, this, this, () => { TickReads++; return 12345; });
        }
        internal Task Start(bool guard, string key = "selection") => guard
            ? Quotes.QuoteGuardAsync(key, "sKiLlEd", (k, message, response) => Receive(k, message, response, response?.BankStatus))
            : Quotes.QuoteAsync(key, "Mining", 14, 9, (k, message, response) => Receive(k, message, response, response?.BankStatus));
        private void Receive(string key, string message, object? response, ModBankStatusResponseDto? bank)
        { Callbacks++; LastKey = key; LastMessage = message; HasQuote = response is not null; if (bank?.OpenDebts?.Count > 0) bank.OpenDebts[0].AmountSilver = 9999; }
        public Task<ClashOfRimClientNetworkResult<ModMercenaryQuoteResponseDto>> QuoteAsync(ClashOfRimClientNetworkContext context,
            long ticks, string skill, int level, int days, CancellationToken token) => Send(context, ticks, skill + ":" + level + ":" + days,
                () => new ModMercenaryQuoteResponseDto { Result = Result(), PriceSilver = Price, BankStatus = ClientBankView.Copy(ReplyBank) });
        public Task<ClashOfRimClientNetworkResult<ModMercenaryGuardQuoteResponseDto>> QuoteGuardAsync(ClashOfRimClientNetworkContext context,
            long ticks, string tier, CancellationToken token) => Send(context, ticks, tier,
                () => new ModMercenaryGuardQuoteResponseDto { Result = Result(), PriceSilver = Price, BankStatus = ClientBankView.Copy(ReplyBank) });
        private async Task<ClashOfRimClientNetworkResult<T>> Send<T>(ClashOfRimClientNetworkContext context, long ticks, string args, Func<T> response)
        {
            Interlocked.Increment(ref Calls); Captured = context; Ticks = ticks; Arguments = args; Sent.TrySetResult();
            if (Block) await Release.Task;
            if (Outcome == "exception") throw new InvalidOperationException("network failure");
            if (Outcome == "cancelled") throw new OperationCanceledException("cancelled");
            return Outcome == "transport" ? ClashOfRimClientNetworkResult<T>.Failed("offline", "failure")
                : ClashOfRimClientNetworkResult<T>.Ok(Outcome == "missing" ? default! : response());
        }
        private ModProtocolResponseDto Result() => new() { Accepted = Outcome != "rejected", ErrorCode = 2, Message = "server-message" };
        public string Text(string key, params (string Name, object? Value)[] args) => key + string.Join(";", args.Select(a => a.Name + "=" + a.Value));
        public void Diagnose(Exception exception) => Diagnostics++;
        public string Summary(ModBankStatusResponseDto? status) => "bank";
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public void Message(string message, bool rejected) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
