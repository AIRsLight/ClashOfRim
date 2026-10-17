using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientBankModuleTests
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
                foreach (ClientBankOperation kind in Enum.GetValues<ClientBankOperation>())
                {
                    var f = new Fixture(kind); int completed = 0;
                    f.Bank.Completed += () => { Check(!f.Sync.IsBusy && !f.Mutations.IsPending, "Completion preceded cleanup."); completed++; };
                    Task work = f.Start();
                    Check(f.Applied == 1 && f.Ports.Captures == 1 && f.State.Busy && f.Sync.IsBusy, "Bank did not retain admission until publication.");
                    (await f.Ports.Next())(); await work;
                    Check(completed == 1 && f.Ports.Snapshot == "accepted" && f.Ports.Published == 1 && !f.State.Busy,
                        "Bank failed authoritative publication: " + kind);
                    Check(f.Amount == (kind == ClientBankOperation.Borrow ? 100 : kind == ClientBankOperation.RepayLoan ? 120 : 70), "Wrong silver amount.");
                    Check(f.Confirmed == (kind == ClientBankOperation.Borrow ? 1 : 0), "Wrong loan quest confirmation.");
                    Check(f.Sent!.Operation == kind && f.Context!.CurrentSnapshotId == "base", "Submission lost its captured intent.");
                }
                foreach (string rejection in new[] { "disabled", "minimum", "maximum", "duration", "map", "silver", "missing-debt" })
                {
                    var f = new Fixture(rejection is "silver" or "missing-debt" ? ClientBankOperation.RepayDebt : ClientBankOperation.Borrow);
                    var view = f.State.View!;
                    if (rejection == "disabled") view.BankLoansEnabled = false;
                    if (rejection == "minimum") view.MinLoanSilver = 101;
                    if (rejection == "maximum") view.ConfiguredMaxLoanSilver = 99;
                    if (rejection == "duration") view.MinDurationDays = 3;
                    if (rejection == "map") f.Facts = f.Facts with { HasLoanMap = false };
                    if (rejection == "silver") f.Facts = f.Facts with { Silver = 69 };
                    if (rejection == "missing-debt") view.OpenDebts.Clear();
                    f.State.View = view;
                    await f.Start();
                    Check(f.Applied == 0 && f.Sent is null && !f.Sync.IsBusy && !f.Mutations.IsPending, "Rejected bank intent mutated: " + rejection);
                }
            }
            foreach (ClientBankOperation kind in Enum.GetValues<ClientBankOperation>())
            {
                await RetryAfterBuild(kind);
                await RetryAfterTransport(kind);
                foreach (string change in new[] { "snapshot", "server", "auth", "colony", "game", "login" })
                {
                    var f = new Fixture(kind);
                    f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task work = f.Start();
                    await f.Submitted.Task;
                    Change(f, change);
                    f.Sync.Reset(); Check(f.Sync.TryBeginManual(out var next), "Replacement could not acquire a lease.");
                    f.Hold.SetResult(f.Success());
                    await work;
                    Check(f.Ports.Published == 0 && f.Confirmed == 0 && f.Ports.Queued == 0 && next!.IsCurrent,
                        "Late response borrowed changed owner or released replacement: " + change);
                    next!.Dispose();
                    var queued = new Fixture(kind);
                    Task waiting = queued.Start(); var deliver = await queued.Ports.Next();
                    Change(queued, change); deliver(); await waiting;
                    Check(queued.Ports.Published == 0 && queued.Confirmed == 0, "Delayed game callback borrowed changed owner: " + change);
                }
                var partial = new Fixture(kind) { ApplyFailure = true };
                await partial.Start(); (await partial.Ports.Next())();
                Check(partial.Applied == 1 && partial.Sent is null && partial.Mutations.IsPending && partial.Failure.Retry is null,
                    "Partial game failure allowed reapplication or cleared pending writes.");
                var thrown = new Fixture(kind) { ApplyException = true };
                await thrown.Start(); (await thrown.Ports.Next())();
                Check(thrown.Mutations.IsPending && thrown.Failure.Retry is null && !thrown.Sync.IsBusy, "Game exception escaped pending recovery.");
            }
            var read = new Fixture(ClientBankOperation.Borrow);
            Task reading = read.Bank.RefreshAsync(); (await read.Ports.Next())(); await reading;
            Check(read.DebtRefreshes == 1 && !read.State.Busy && read.Applied == 0, "Bank refresh applied writes or leaked admission.");
            var copy = read.State.View!; copy.OpenDebts[0].AmountSilver = 999; copy.InterestDurationMultiplierCurve[0].Multiplier = 999;
            Check(read.State.View!.OpenDebts[0].AmountSilver == 70 && read.State.View.InterestDurationMultiplierCurve[0].Multiplier == 1,
                "UI view shares mutable debt or term data.");
            read.Ports.GameOwner = new();
            Check(read.State.View is null && read.State.Message == "StatusNotPulled", "New game inherited bank view.");
            var terms = new ModBankStatusResponseDto { BaseAnnualInterestRate = .1f,
                InterestDurationMultiplierCurve = new() { new() { DurationDays = 60, Multiplier = 1 } } };
            int beforeCurveChange = ClientBankTerms.CalculateInterestSilver(100, 30, terms);
            terms.InterestDurationMultiplierCurve.Add(new() { DurationDays = 0, Multiplier = 3 });
            Check(ClientBankTerms.CalculateInterestSilver(100, 30, terms) > beforeCurveChange, "Interest reused a stale mutable curve cache.");
            var publication = new Fixture(ClientBankOperation.Borrow) { ConfirmException = true };
            Task publishing = publication.Start(); var publish = await publication.Ports.Next();
            try { publish(); } catch (InvalidOperationException) { }
            await publishing; (await publication.Ports.Next())();
            Check(publication.Ports.Published == 1 && publication.Mutations.IsPending && publication.Failure.Retry is null
                && !publication.Sync.IsBusy, "Partial publication cleared pending mutation or repeated the transaction.");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static async Task RetryAfterBuild(ClientBankOperation kind)
    {
        var f = new Fixture(kind);
        f.Ports.CaptureFailure = new("BuildFailure", "injected", SnapshotUploadMessage.BuildFailed);
        await f.Start(); (await f.Ports.Next())();
        Check(f.Failure.Retry is not null && f.Applied == 1 && !f.Sync.IsBusy && f.Mutations.IsPending, "Build failure lost its retry.");
        var retry = f.Failure.Retry!;
        f.Ports.CaptureFailure = null;
        retry(); (await f.Ports.Next())();
        Check(f.Applied == 1 && f.Ports.Captures == 2 && f.Ports.Published == 1 && !f.Mutations.IsPending, "Rebuild re-applied a bank mutation.");
        retry();
        Check(f.Applied == 1 && f.Ports.Captures == 2, "Old recovery window replayed twice.");
    }

    private static async Task RetryAfterTransport(ClientBankOperation kind)
    {
        var f = new Fixture(kind) { TransportFailure = true };
        await f.Start(); (await f.Ports.Next())();
        var command = f.Sent; var snapshot = f.Package; var payload = f.Payload;
        Check(f.Failure.Retry is not null && f.Ports.Captures == 1, "Transport failure is not retryable.");
        f.TransportFailure = false;
        f.Failure.Retry!(); (await f.Ports.Next())();
        Check(ReferenceEquals(command, f.Sent) && ReferenceEquals(snapshot, f.Package) && ReferenceEquals(payload, f.Payload)
            && f.Applied == 1 && f.Ports.Captures == 1 && f.Ports.Published == 1, "Retry rebuilt or rebound a prepared transaction.");

        var stale = new Fixture(kind) { TransportFailure = true };
        await stale.Start(); (await stale.Ports.Next())(); var retry = stale.Failure.Retry!;
        stale.Ports.Snapshot = "new-base";
        retry();
        Check(stale.Submissions == 1 && stale.Ports.Captures == 1, "Retry borrowed a new base snapshot.");
        (await stale.Ports.Next())();
        Check(stale.Failure.Retry is null && stale.Failure.Exit is not null && stale.Mutations.IsPending,
            "Obsolete retry lost the authoritative-reload decision.");
    }

    private static void Change(Fixture f, string change)
    {
        switch (change)
        {
            case "snapshot": f.Ports.Snapshot = "new-base"; break;
            case "server": f.Ports.Server = "http://new-server"; break;
            case "auth": f.Ports.Auth = "new-auth"; break;
            case "colony": f.Ports.Colony = "new-colony"; break;
            case "game": f.Ports.GameOwner = new(); break;
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "new-login" }); break;
        }
    }

    private sealed class Fixture : IClientBankRuntime, IClientBankTransport, IClientBankPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly EventModuleFailureView Failure;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientBankState State;
        internal readonly ClientBankOperations Bank;
        internal readonly ClientBankOperation Kind;
        internal ClientBankFacts Facts = new(1000, 10000, 1000, true);
        internal int Applied, Amount, Confirmed, DebtRefreshes, Submissions;
        internal bool ApplyFailure, ApplyException, TransportFailure, ConfirmException;
        internal ClientBankCommand? Sent;
        internal ClashOfRimClientNetworkContext? Context;
        internal ModSnapshotPackageMetadataDto? Package;
        internal byte[]? Payload;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ClientBankReply>>? Hold;
        internal readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Fixture(ClientBankOperation kind)
        {
            ClientSessionWork.Reset(); Kind = kind;
            var connection = new ClientSessionConnection(Ports);
            Session = new ClientSessionLifecycle(Ports, Ports, connection);
            Failure = new EventModuleFailureView(Ports);
            var recovery = new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, Failure);
            State = new ClientBankState(Session, Ports, this);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports, Ports));
            Bank = new ClientBankOperations(new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions), Sync, Mutations,
                transactions, recovery, State, this, this, this);
            State.View = new() { BankLoansEnabled = true, MinLoanSilver = 10, ConfiguredMaxLoanSilver = 500, MaxLoanWealthRatio = .5f,
                MinDurationDays = 1, MaxDurationDays = 7, BaseAnnualInterestRate = .1f,
                ActiveLoan = kind == ClientBankOperation.RepayLoan ? new() { LoanId = "loan", TotalDueSilver = 110, Status = "Active" } : null,
                OpenDebts = new() { new() { DebtId = "debt", AmountSilver = 70, Status = "Active" } },
                InterestDurationMultiplierCurve = new() { new() { DurationDays = 1, Multiplier = 1 } } };
        }
        internal Task Start() => Kind switch { ClientBankOperation.Borrow => Bank.BorrowAsync(100, 2),
            ClientBankOperation.RepayLoan => Bank.RepayLoanAsync(), _ => Bank.RepayDebtAsync("debt") };
        public ClientBankFacts ReadFacts() => Facts;
        public int LoanTotalDue(string id, int amount) => 120;
        public ClientBankApplication Apply(ClientBankCommand command, ModBankStatusResponseDto pending)
        {
            Applied++; Amount = command.Silver;
            if (ApplyException) throw new InvalidOperationException("after partial mutation");
            return new(!ApplyFailure, "partial mutation");
        }
        public void ConfirmLoan(ModBankLoanSummaryDto loan, ModBankStatusResponseDto status)
        { Confirmed++; if (ConfirmException) throw new InvalidOperationException("injected quest update failure"); }
        public void RefreshDebts(ModBankStatusResponseDto status) => DebtRefreshes++;
        public Task<ClashOfRimClientNetworkResult<ModBankStatusResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, long ticks, int wealth, CancellationToken token) =>
            Task.FromResult(ClashOfRimClientNetworkResult<ModBankStatusResponseDto>.Ok(State.View!));
        public Task<ClashOfRimClientNetworkResult<ClientBankReply>> SubmitAsync(ClashOfRimClientNetworkContext context, ClientBankCommand command,
            ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
        {
            Context = context; Sent = command; Package = snapshot; Payload = payload; Submissions++;
            Submitted.TrySetResult();
            return Hold?.Task ?? Task.FromResult(TransportFailure ? ClashOfRimClientNetworkResult<ClientBankReply>.Failed("HttpRequestException", "offline") : Success());
        }
        internal ClashOfRimClientNetworkResult<ClientBankReply> Success() => ClashOfRimClientNetworkResult<ClientBankReply>.Ok(new(
            new() { Accepted = true }, null, Sent?.Loan, "accepted", "lineage"));
        public string Text(string key, params (string Name, object? Value)[] arguments) => key;
        public string Summary(ModBankStatusResponseDto? status) => "summary";
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public void Message(string message, bool rejected) { }
        public void Diagnose(Exception exception) { }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
