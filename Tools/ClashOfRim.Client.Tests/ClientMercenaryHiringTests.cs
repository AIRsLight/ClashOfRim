using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientMercenaryHiringTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (bool guard in new[] { false, true })
            {
                foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
                { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); await Success(guard); }
                foreach (Fault fault in Enum.GetValues<Fault>()) if (fault != Fault.None) await Failure(guard, fault);
                foreach (string retry in new[] { "build", "upload", "arrival" }) await Retry(guard, retry);
                foreach (string change in new[] { "user", "colony", "auth", "server", "snapshot", "game", "login", "generation", "reset" })
                foreach (string phase in new[] { "quote", "queued-quote", "payment", "queued-payment" }) await Late(guard, change, phase);
                await Admission(guard); await RetryOwnership(guard); await Confirmation(guard);
            }
            await ArrivalException(); await PartialPublication(); await CompletionStartsNewWork();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Success(bool guard)
    {
        using var f = new Fixture(guard);
        Task work = f.Start(); var prepare = await f.Ports.Next();
        Require(f.Applied == 0 && f.Sync.ManualInProgress && f.State.Busy && !f.Mutations.IsPending, "Quote lost admission or mutated before game-thread preparation.");
        prepare(); var publish = await f.Ports.Next();
        Require(f.Applied == 1 && f.Sync.SnapshotInProgress && f.State.Busy && f.Mutations.IsPending && f.Ports.Captures == 1, "Payment lost ownership before publication.");
        publish(); await work;
        Require(f.Submissions == 1 && f.Completed == 1 && !f.State.Busy && !f.Sync.IsBusy && !f.Mutations.IsPending, "Hire did not complete exact lease/mutation cleanup.");
        Require(f.Context!.AuthToken == "auth" && f.Context.CurrentSnapshotId == "base" && f.Command!.GameTicks == 300
            && f.Command.Terms.Price == 50 && f.Ports.Snapshot == f.Package!.SnapshotId, "Hire lost captured identity, time or terms.");
        Require(f.State.Message.StartsWith(guard ? "GuardStatusConfirmed" : "StatusHiredConfirmed", StringComparison.Ordinal), "Hire confused guard and pawn success.");
        var command = f.Command!;
        if (guard) Require(command.GuardContract().PointRatio == 1.5f && command.GuardContract().Tier == "Elite", "Guard changed quote terms.");
        else Require(command.Contract().ExpiresAtGameTicks == 420300 && command.Contract().HarmfulSurgeryFineSilver == 20, "Hire changed expiry/fines.");
        var mutable = command.Contract(); mutable.ContractId = "modified";
        Require(command.Contract().ContractId != "modified", "Command shared its mutable DTO with the engine.");
    }
    private static async Task Failure(bool guard, Fault fault)
    {
        if (!guard && fault == Fault.InvalidRatio) return;
        using var f = new Fixture(guard) { Failure = fault };
        Task work = f.Start();
        bool quoteThrows = fault == Fault.QuoteException;
        if (!quoteThrows)
        {
            var prepare = await f.Ports.Next();
            try { prepare(); } catch (InvalidOperationException) when (fault is Fault.BeforeEffectException or Fault.PartialException) { }
        }
        bool changed = fault is Fault.PartialFailure or Fault.PartialException or Fault.Transport or Fault.Forbidden or Fault.Rejected or Fault.MissingContract or Fault.WrongContract or Fault.MissingSnapshot or Fault.WrongSnapshot;
        if (fault is Fault.MissingContract or Fault.WrongContract or Fault.MissingSnapshot or Fault.WrongSnapshot) (await f.Ports.Next())();
        await work;
        if (changed) (await f.Ports.Next())();
        Require(f.Ports.Published == 0 && f.Completed == 0 && !f.State.Busy && !f.Sync.IsBusy, "Failed hire published or stranded lease: " + fault);
        Require(f.Mutations.IsPending == changed && (f.View.Retry is not null) == (fault == Fault.Transport), "Failure replay classification wrong: " + fault);
        if (fault is Fault.PartialFailure or Fault.PartialException or Fault.BeforeEffectException)
            Require(f.Submissions == 0 && f.Ports.Captures == 0, "Partially prepared mercenary was submitted.");
    }
    private static async Task Retry(bool guard, string kind)
    {
        if (guard && kind == "arrival") return;
        using var f = new Fixture(guard) { Failure = kind == "upload" ? Fault.Transport : Fault.None, AwaitArrival = kind == "arrival" };
        if (kind == "build") f.Ports.CaptureFailure = new("capture", "failed", SnapshotUploadMessage.SaveFailed);
        Task work = f.Start(); (await f.Ports.Next())(); await work; (await f.Ports.Next())();
        Require(f.View.Retry is not null && f.Applied == 1, "Prepared failure lost retry.");
        var command = f.Command; var package = f.Package; var payload = f.Payload; int captures = f.Ports.Captures;
        f.Failure = Fault.None; f.AwaitArrival = false; f.Ports.CaptureFailure = null;
        var retry = f.View.Retry!; retry(); retry();
        (await f.Ports.Next())(); await f.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Applied == 1 && f.Quotes == 1 && f.Resumed == (kind == "arrival" ? 1 : 0) && f.Submissions == (kind == "upload" ? 2 : 1), "Retry repeated quote/debit/pawn creation.");
        Require(f.Ports.Captures == captures + (kind == "upload" ? 0 : 1), "Retry rebuilt prepared bytes.");
        if (kind == "upload") Require(ReferenceEquals(command, f.Command) && ReferenceEquals(package, f.Package) && ReferenceEquals(payload, f.Payload), "Payment retry changed intent or bytes.");
    }
    private static async Task Late(bool guard, string change, string phase)
    {
        using var f = new Fixture(guard);
        if (phase == "quote") f.QuoteHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (phase == "payment") f.PaymentHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = f.Start();
        Action? queued = null;
        if (phase == "quote") await f.Quoted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else if (phase == "queued-quote") queued = await f.Ports.Next();
        else
        {
            (await f.Ports.Next())();
            if (phase == "payment") await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else queued = await f.Ports.Next();
        }
        f.Change(change);
        void Deliver()
        {
            if (queued is not null) queued();
            else if (phase == "quote") f.QuoteHold!.SetResult(f.Offer());
            else f.PaymentHold!.SetResult(f.Receipt());
        }
        if (change == "snapshot")
        {
            // A save advance keeps the domain scope but invalidates this payment's
            // original base. Do not reset state and accidentally mask that check.
            Deliver(); await work;
            Require(f.Ports.Published == 0 && f.Completed == 0 && !f.State.Busy && !f.Sync.IsBusy, "Old base snapshot still published or retained its lease.");
            return;
        }
        Require(!f.State.Busy && !f.Sync.IsBusy, "Identity/Game change did not invalidate the request by itself: " + change);
        f.Sync.TryBeginManual(out var replacement); var newer = f.State.Requests.Begin(replacement)!;
        Deliver();
        await work;
        Require(f.Ports.Published == 0 && f.Completed == 0 && f.State.Busy && replacement!.IsCurrent, "Old " + phase + "/" + change + " modified new work.");
        Require(f.Applied == (phase is "quote" or "queued-quote" ? 0 : 1), "Late quote prepared local state.");
        f.State.Requests.End(newer);
    }
    private static async Task Admission(bool guard)
    {
        foreach (string reason in new[] { "unconfigured", "snapshot", "pending", "busy", "domain-busy", "map" })
        {
            using var f = new Fixture(guard);
            switch (reason)
            {
                case "unconfigured": f.Ports.User = ""; break; case "snapshot": f.Ports.Snapshot = ""; break;
                case "pending": f.Mutations.Begin(LocalMutationKind.Bank, "pending"); break;
                case "busy": f.Sync.TryBeginManual(out _); break; case "domain-busy": f.State.Requests.Begin(); break;
                case "map": f.NoMap = true; break;
            }
            await f.Start();
            Require(f.Applied == 0 && f.Quotes == 0 && f.Submissions == 0, "Admission allowed hire: " + reason);
            if (reason == "pending") Require(f.Mutations.Current?.Kind == LocalMutationKind.Bank, "Rejected hire cleared a foreign mutation.");
            if (reason == "busy") Require(f.Sync.IsBusy, "Rejected hire released another lease.");
        }
    }
    private static async Task RetryOwnership(bool guard)
    {
        using var f = new Fixture(guard) { Failure = Fault.Transport };
        Task work = f.Start(); (await f.Ports.Next())(); await work; (await f.Ports.Next())();
        f.Ports.Snapshot = "new-base"; f.View.Retry!(); (await f.Ports.Next())();
        Require(f.View.Retry is null && f.View.Message == "base-changed" && f.Submissions == 1 && f.Mutations.IsPending, "Retry borrowed a new snapshot.");
        using var busy = new Fixture(guard) { Failure = Fault.Transport };
        work = busy.Start(); (await busy.Ports.Next())(); await work; (await busy.Ports.Next())();
        busy.Sync.TryBeginManual(out var other); busy.View.Retry!(); (await busy.Ports.Next())();
        Require(busy.View.Retry is not null && other!.IsCurrent && busy.Submissions == 1, "Busy retry released a foreign lease.");
        other!.Dispose(); var old = busy.View.Retry!; busy.Change("login"); old();
        Require(busy.Submissions == 1, "Old failure retried under a new login.");
    }
    private static async Task Confirmation(bool guard)
    {
        using var f = new Fixture(guard);
        Action bind() => guard ? f.Hiring.BindGuard("Elite") : f.Hiring.BindHire("Shooting", 12, 7);
        var old = bind(); f.Change("login"); old();
        Require(f.Quotes == 0, "Old confirmation dialog borrowed new login.");
        var current = bind(); f.Ports.Snapshot = "next-base"; current();
        (await f.Ports.Next())(); (await f.Ports.Next())(); await f.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Context!.CurrentSnapshotId == "next-base", "A new confirmed command used an old base snapshot.");
    }
    private static async Task ArrivalException()
    {
        using var f = new Fixture(false) { AwaitArrival = true };
        Task work = f.Start(); (await f.Ports.Next())(); await work; (await f.Ports.Next())();
        f.ResumeThrows = true; f.View.Retry!(); (await f.Ports.Next())();
        Require(f.View.Retry is null && f.Mutations.IsPending && f.Submissions == 0 && f.Applied == 1 && f.Resumed == 1, "Uncertain arrival can be replayed.");
    }
    private static async Task PartialPublication()
    {
        using var f = new Fixture(false);
        f.Ports.OnPublish = () => throw new System.IO.IOException("publish failed");
        Task work = f.Start(); (await f.Ports.Next())();
        try { (await f.Ports.Next())(); } catch (System.IO.IOException) { }
        await work; (await f.Ports.Next())();
        Require(f.Ports.Published == 1 && f.Completed == 0 && f.Mutations.IsPending && f.View.Retry is null && !f.State.Busy, "Partial publication offered another charge.");
    }
    private static async Task CompletionStartsNewWork()
    {
        using var f = new Fixture(false);
        ClientGameRequestTracker.Request? next = null;
        ClientSynchronizationCoordinator.Lease? lease = null;
        f.Hiring.Completed += () => { Require(f.Sync.TryBeginManual(out lease), "Completion could not acquire the next lease."); next = f.State.Requests.Begin(lease); };
        Task work = f.Start(); (await f.Ports.Next())(); (await f.Ports.Next())(); await work;
        Require(next is not null && f.State.Busy && lease!.IsCurrent, "Old finally ended work started by completion.");
        f.State.Requests.End(next!);
    }

    internal enum Fault { None, QuoteTransport, QuoteRejected, QuoteException, InvalidPrice, InvalidRatio, InsufficientSilver,
        BeforeEffectException, PartialFailure, PartialException, Transport, Forbidden, Rejected, MissingContract, WrongContract, MissingSnapshot, WrongSnapshot }
    internal sealed class Fixture : IDisposable, IClientMercenaryRuntime, IClientMercenarySource, IClientMercenaryHiringTransport, IClientMercenaryHiringPresentation, IClientBankPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientMercenaryState State;
        internal readonly ClientBankState Bank;
        internal readonly ClientMercenaryHiring Hiring;
        internal readonly EventModuleFailureView View;
        private readonly bool guard;
        internal Fault Failure;
        internal bool NoMap, AwaitArrival, ResumeThrows;
        internal int Applied, Resumed, Quotes, Submissions, Completed;
        internal ClientMercenaryCommand? Command;
        internal ClashOfRimClientNetworkContext? Context;
        internal ModSnapshotPackageMetadataDto? Package;
        internal byte[]? Payload;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ClientMercenaryOffer>>? QuoteHold;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ClientMercenaryHireReply>>? PaymentHold;
        internal readonly TaskCompletionSource Quoted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture(bool guard)
        {
            this.guard = guard; ClientSessionWork.Reset();
            Session = new(Ports, Ports, new ClientSessionConnection(Ports)); State = new(Session, Ports, this); View = new(Ports);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports, Ports));
            Bank = new(Session, Ports, this);
            Hiring = new(State, Bank, new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions),
                Sync, Mutations, transactions, new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, View), this, this, this);
            Hiring.Completed += () => { Require(!State.Busy && !Sync.IsBusy && !Mutations.IsPending, "Completed before releasing own work."); Completed++; Done.TrySetResult(); };
        }
        internal Task Start() => Hiring.StartAsync(new("Shooting", 12, guard ? 0 : 7, guard ? "Elite" : null));
        public IClientMercenarySource Capture() => this;
        public long GameTicks => 300;
        public int TicksPerDay => 60000;
        public string? Validate(int price) => NoMap ? "map missing" : price > 0 && Failure == Fault.InsufficientSilver ? "insufficient silver" : null;
        public ClientMercenaryLocalResult Prepare(ClientMercenaryCommand command, Action mutationStarting)
        {
            Require(State.Busy && Sync.ManualInProgress && Mutations.IsPending, "Preparation outside manual admission.");
            Applied++; Command = command;
            if (Failure == Fault.BeforeEffectException) throw new InvalidOperationException("before effects");
            mutationStarting();
            if (Failure == Fault.PartialException) throw new InvalidOperationException("partial");
            return new(Failure == Fault.PartialFailure ? ClientMercenaryPreparation.Failed : AwaitArrival ? ClientMercenaryPreparation.AwaitingArrival : ClientMercenaryPreparation.Prepared, "local failure");
        }
        public ClientMercenaryLocalResult ResumeArrival()
        { Resumed++; if (ResumeThrows) throw new InvalidOperationException("arrival"); return new(ClientMercenaryPreparation.Prepared, ""); }
        public Task<ClashOfRimClientNetworkResult<ClientMercenaryOffer>> QuoteAsync(ClashOfRimClientNetworkContext context, ClientMercenarySelection selection, long ticks, CancellationToken token)
        {
            Quotes++; Context = context; Quoted.TrySetResult();
            if (Failure == Fault.QuoteException) throw new InvalidOperationException("quote");
            return QuoteHold?.Task ?? Task.FromResult(Offer());
        }
        internal ClashOfRimClientNetworkResult<ClientMercenaryOffer> Offer() => Failure == Fault.QuoteTransport
            ? ClashOfRimClientNetworkResult<ClientMercenaryOffer>.Failed("HttpError", "503 unavailable")
            : ClashOfRimClientNetworkResult<ClientMercenaryOffer>.Ok(new(new() { Accepted = Failure != Fault.QuoteRejected }, new(), Failure == Fault.InvalidPrice ? 0 : 50,
                20, 100, "Elite", Failure == Fault.InvalidRatio ? float.NaN : 1.5f));
        public Task<ClashOfRimClientNetworkResult<ClientMercenaryHireReply>> SubmitAsync(ClashOfRimClientNetworkContext context, ClientMercenaryCommand command,
            ModSnapshotPackageMetadataDto package, byte[] payload, CancellationToken token)
        {
            Context = context; Command = command; Package = package; Payload = payload; Submissions++; Sent.TrySetResult();
            return PaymentHold?.Task ?? Task.FromResult(Failure is Fault.Transport or Fault.Forbidden
                ? ClashOfRimClientNetworkResult<ClientMercenaryHireReply>.Failed("HttpError", Failure == Fault.Forbidden ? "403 forbidden" : "503 unavailable") : Receipt());
        }
        internal ClashOfRimClientNetworkResult<ClientMercenaryHireReply> Receipt() => ClashOfRimClientNetworkResult<ClientMercenaryHireReply>.Ok(
            new(new() { Accepted = Failure != Fault.Rejected }, new(), Failure == Fault.MissingContract ? null : Failure == Fault.WrongContract ? "other" : Command!.ContractId,
                Failure == Fault.MissingSnapshot ? null : Failure == Fault.WrongSnapshot ? "other" : Package!.SnapshotId, "lineage"));
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "new"; break; case "colony": Ports.Colony = "new"; break;
                case "auth": Ports.Auth = "new"; break; case "server": Ports.Server = "http://new"; break;
                case "snapshot": Ports.Snapshot = "new"; break; case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth }); break;
                // Session exit resets global synchronization explicitly; domain
                // state must still notice the changed generation without Reset.
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); break; case "reset": State.Reset(); break;
            }
        }
        public string Idle => "idle";
        public string BaseSnapshotChanged => "base-changed";
        public string Text(string key, params (string Name, object? Value)[] args) => key + string.Join("", args.Select(a => ":" + a.Name + "=" + a.Value));
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public string Summary(ModBankStatusResponseDto? status) => "summary";
        public void Message(string message, bool rejected) { }
        public void Diagnose(Exception exception) { }
        public void Dispose() { Sync.Reset(); ClientSessionWork.Reset(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
