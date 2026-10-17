using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientMercenaryIncidentsTests
{
    internal static void Run()
    {
        try { RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { throw new InvalidOperationException(ex.ToString(), ex); }
    }
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (string kind in new[] { "Death", "HostileBehavior", "HarmfulSurgery", "OvertimeService", "ShuttleDestroyed", "Completed" })
            { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); await Success(kind); }
            await Sequence(); await WaitForForeignWork(); await ReportRetry(); await ConfirmationRetry();
            foreach (Fault fault in Enum.GetValues<Fault>()) if (fault != Fault.None) await Failure(fault);
            foreach (string change in new[] { "user", "colony", "auth", "server", "snapshot", "game", "login", "generation", "reset" })
            foreach (bool queued in new[] { false, true }) await Late(change, queued);
            await QueuedOwnerReplacement(); await Admission(); await RetryBase(); await ExitCancelsQueue();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Success(string kind)
    {
        using var f = new Fixture();
        Task first = f.Report(kind, "key");
        await f.Step();
        if (kind != "Completed")
        {
            Require(f.Applied.Count == 1 && f.Ports.Captures == 0 && f.Sync.SnapshotInProgress && f.State.Busy && f.Mutations.IsPending, "Incident lost admission between debt and capture.");
            await f.Step(); await f.Step(); await f.Step();
        }
        await first;
        Require(f.Commands.Single().Kind == kind && f.Commands[0].Key == "key" && f.Commands[0].GameTicks == 1234
            && f.Contexts.Single().AuthToken == "auth" && f.Refreshes == 1 && !f.Sync.IsBusy && !f.Mutations.IsPending && !f.State.Busy, "Incident command or completion changed.");
        Require(f.Uploads == (kind == "Completed" ? 0 : 1) && f.Notices == f.Uploads, "Completed incident repeated confirmation/negative message.");
        if (kind != "Completed") Require(f.Bank.View!.TotalOpenDebtSilver == 50 && f.Bank.View.OpenDebts.Single().AmountSilver == 50, "Engine debt callback mutated bank cache.");
    }
    private static async Task Sequence()
    {
        using var f = new Fixture();
        Task first = f.Report("OvertimeService", "day-1"), second = f.Report("OvertimeService", "day-2");
        await f.Step(); await f.Step();
        Require(f.Commands.Count == 1 && f.CapturedDebts.Single().SequenceEqual(new[] { "debt:day-1" }), "Second incident overtook first capture.");
        await f.Step(); await f.Step(); await first;
        await f.Step(); await f.Step(); await f.Step(); await f.Step(); await second;
        Require(f.Commands.Select(c => c.Key).SequenceEqual(new[] { "day-1", "day-2" }) && f.Contexts[1].CurrentSnapshotId == "accepted-1"
            && f.CapturedDebts[1].SequenceEqual(new[] { "debt:day-1", "debt:day-2" }) && f.Uploads == 2, "Queued incident reused an obsolete base or missed debt confirmation.");
    }
    private static async Task WaitForForeignWork()
    {
        using var f = new Fixture();
        f.Mutations.Begin(LocalMutationKind.Bank, "bank"); f.Sync.TryBeginManual(out var foreign);
        Task queued = f.Report("Death", "queued");
        await Task.Yield();
        Require(f.Commands.Count == 0 && foreign!.IsCurrent && f.Mutations.Current!.Kind == LocalMutationKind.Bank, "Queued incident disturbed foreign recovery.");
        foreign!.Dispose();
        Require(f.Commands.Count == 0, "Incident ignored unresolved foreign mutation.");
        f.Mutations.Reset();
        await f.Step(); await f.Step(); await f.Step(); await f.Step(); await queued;
        Require(f.Refreshes == 1, "Queued incident failed to resume after foreign completion.");
    }
    private static async Task ReportRetry()
    {
        using var f = new Fixture { Failure = Fault.Transport };
        Task first = f.Report("Death", "stable"), second = f.Report("OvertimeService", "later");
        await f.Step(); await first; await f.Step();
        Require(f.View.Retry is not null && f.Commands.Count == 1 && !f.State.Busy && !f.Sync.IsBusy && f.Mutations.IsPending, "Report failure lost recoverable state or admitted next incident.");
        f.Failure = Fault.None; var retry = f.View.Retry!; retry(); retry();
        await f.Step(); await f.Step(); await f.Step(); await f.Step();
        // Recovery closes its existing failure window before the queued incident.
        await f.Step();
        await f.Step(); await f.Step(); await f.Step(); await f.Step(); await second;
        Require(f.Commands.Count == 3 && f.Commands[0] == f.Commands[1] && f.Applied.Count == 2 && f.Uploads == 2, "Report retry changed intent or replayed debt/queued work.");
    }
    private static async Task ConfirmationRetry()
    {
        using var f = new Fixture { Failure = Fault.Upload };
        Task first = f.Report("Death", "stable"), second = f.Report("OvertimeService", "later");
        await f.Step(); await f.Step(); await f.Step(); await first; await f.Step();
        Require(f.View.Retry is not null && f.Commands.Count == 1 && f.Applied.Count == 1, "Confirmation failure did not pause incident queue.");
        f.Failure = Fault.None; f.View.Retry!();
        await f.Step(); await f.Step(); await f.Step(); await f.Step();
        await f.Step(); await f.Step(); await f.Step(); await f.Step(); await second;
        Require(f.Commands.Count == 2 && f.Applied.Count == 2 && f.Uploads == 3, "Snapshot retry resubmitted the incident or duplicated local debt.");
    }
    private static async Task Failure(Fault fault)
    {
        using var f = new Fixture { Failure = fault };
        Task first = f.Report("Death", "fault");
        if (fault != Fault.Exception)
        {
            try { await f.Step(); }
            catch (IOException) when (fault == Fault.Apply) { }
        }
        if (fault == Fault.Upload) { await f.Step(); await f.Step(); }
        await first; await f.Step();
        Require(f.Ports.Published == 0 && f.Refreshes == 0 && !f.State.Busy && !f.Sync.IsBusy && f.Mutations.IsPending, "Failed incident cleared pending state: " + fault);
        bool retry = fault is Fault.Transport or Fault.Exception or Fault.Upload;
        Require((f.View.Retry is not null) == retry, "Incident retry classification incorrect: " + fault);
        if (fault is not (Fault.Apply or Fault.Upload)) Require(f.Applied.Count == 0 && f.Ports.Captures == 0, "Rejected/incomplete incident changed game state.");
    }
    private static async Task Late(string change, bool queued)
    {
        using var f = new Fixture();
        var hold = new TaskCompletionSource<ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queued) f.Hold = hold;
        Task first = f.Report("Death", "old");
        Action? delayed = queued ? await f.Ports.Next() : null;
        if (!queued) await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Change(change);
        if (queued) delayed!(); else hold.SetResult(f.Response(f.Commands[0]));
        await first;
        Require(f.Applied.Count == 0 && f.Ports.Published == 0 && f.Refreshes == 0, "Old incident crossed " + change + " ownership.");
    }
    private static async Task QueuedOwnerReplacement()
    {
        using var f = new Fixture();
        var oldReply = new TaskCompletionSource<ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Hold = oldReply;
        Task old = f.Report("Death", "old"), obsoleteQueued = f.Report("Death", "old-queued");
        await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Change("login"); f.Mutations.Reset();
        await obsoleteQueued.WaitAsync(TimeSpan.FromSeconds(5));
        Task current = f.Report("Completed", "current"); await f.Step(); await current;
        Require(f.Commands.Count == 2 && f.Refreshes == 1 && f.Commands[1].Key == "current", "New login waited for or executed old queued incident.");
        oldReply.SetResult(f.Response(f.Commands[0])); await old; await obsoleteQueued;
        Require(f.Applied.Count == 0 && f.Refreshes == 1 && !f.Sync.IsBusy, "Late old report disturbed new session.");
    }
    private static async Task Admission()
    {
        foreach (string reason in new[] { "contract", "kind", "unconfigured", "snapshot" })
        {
            using var f = new Fixture();
            if (reason == "unconfigured") f.Ports.User = "";
            if (reason == "snapshot") f.Ports.Snapshot = "";
            await f.Incidents.ReportAsync(reason == "contract" ? "" : "contract", reason == "kind" ? "" : "Death");
            Require(f.Commands.Count == 0 && !f.Mutations.IsPending && !f.Sync.IsBusy, "Invalid incident entered admission.");
        }
    }
    private static async Task RetryBase()
    {
        using var f = new Fixture { Failure = Fault.Transport };
        Task first = f.Report("Death", "key"); await f.Step(); await first; await f.Step();
        f.Ports.Snapshot = "new"; f.View.Retry!(); await f.Step();
        Require(f.View.Retry is null && f.View.Message == "base-changed" && f.Commands.Count == 1, "Incident retry borrowed a new base.");
    }
    private static async Task ExitCancelsQueue()
    {
        using var f = new Fixture { Failure = Fault.Transport };
        Task first = f.Report("Death", "key"), next = f.Report("Death", "queued");
        await f.Step(); await first; await f.Step(); f.View.Exit!();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Commands.Count == 1 && f.View.Menus == 1 && !f.Sync.IsBusy, "Returning to menu dispatched queued incidents.");
    }
    private enum Fault { None, Transport, Exception, Rejected, Forbidden, MissingStatus, MissingDebtId, Apply, Upload }
    private sealed class Fixture : IDisposable, IClientMercenaryIncidentRuntime, IClientMercenaryIncidentTransport,
        IClientMercenaryHiringPresentation, IClientMercenaryIncidentPresentation, IClientBankPresentation, IClientSnapshotTransport, IClientLocalSnapshotPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientMercenaryState State;
        internal readonly ClientBankState Bank;
        internal readonly ClientMercenaryIncidents Incidents;
        internal readonly EventModuleFailureView View;
        internal readonly List<ClientMercenaryIncidentCommand> Commands = new();
        internal readonly List<ClashOfRimClientNetworkContext> Contexts = new();
        internal readonly List<string> Applied = new();
        internal readonly List<string[]> CapturedDebts = new();
        internal int Uploads, Refreshes, Notices;
        internal Fault Failure;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>>? Hold;
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports)); State = new(Session, Ports, this); Bank = new(Session, Ports, this); View = new(Ports);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports, Ports));
            var admission = new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions);
            var recovery = new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, View);
            var local = new ClientLocalSnapshotConfirmation(Session, Ports, admission, Sync, Mutations, transactions, recovery, new ClientSnapshotUploader(Ports, Ports, this, Sync), this);
            var confirmation = new ClientMercenaryConfirmation(local, State, this);
            Incidents = new(Session, State, Bank, Sync, Mutations, admission, transactions, recovery, confirmation, this, this, this);
            confirmation.BankRefreshRequested += () => { Require(!Sync.IsBusy, "Bank refresh ran under incident lease."); Refreshes++; };
            Incidents.BankRefreshRequested += () => { Require(!Sync.IsBusy, "Completed report retained lease."); Refreshes++; };
            Ports.OnCapture = () => CapturedDebts.Add(Applied.ToArray());
        }
        internal Task Report(string kind, string key) => Incidents.ReportAsync("contract", kind, key);
        internal async Task Step() => (await Ports.Next())();
        public long GameTicks => 1234;
        public void ApplyDebt(ModBankDebtSummaryDto debt, ModBankStatusResponseDto status)
        {
            Applied.Add(debt.DebtId);
            if (Failure == Fault.Apply) throw new IOException("partial quest");
            debt.AmountSilver = 999; status.OpenDebts.Clear();
        }
        public Task<ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>> ReportAsync(ClashOfRimClientNetworkContext context, ClientMercenaryIncidentCommand command, CancellationToken token)
        {
            Require(Sync.SnapshotInProgress && Mutations.IsPending && State.Busy, "Incident HTTP ran outside owned admission.");
            Commands.Add(command); Contexts.Add(context); Sent.TrySetResult();
            if (Failure == Fault.Exception) throw new IOException("network");
            var hold = Hold; Hold = null;
            return hold?.Task ?? Task.FromResult(Response(command));
        }
        internal ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto> Response(ClientMercenaryIncidentCommand command)
        {
            if (Failure is Fault.Transport or Fault.Forbidden)
                return ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>.Failed("HttpError", Failure == Fault.Transport ? "503 unavailable" : "403 forbidden");
            var debt = command.IsCompletion ? null : new ModBankDebtSummaryDto { DebtId = Failure == Fault.MissingDebtId ? "" : "debt:" + command.Key, AmountSilver = 50, SourceId = command.ContractId, Status = "PendingActivation" };
            return ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>.Ok(new() { Result = new() { Accepted = Failure != Fault.Rejected }, Debt = debt,
                BankStatus = Failure == Fault.MissingStatus ? null : new() { TotalOpenDebtSilver = debt is null ? 0 : 50, OpenDebts = debt is null ? new() : new() { debt } } });
        }
        public Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string key,
            ModSnapshotPackageMetadataDto package, byte[] payload, string? operation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements, CancellationToken token)
        {
            Uploads++;
            return Task.FromResult(Failure == Fault.Upload ? ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Failed("HttpError", "503 unavailable")
                : ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Ok(new() { Result = new() { Accepted = true }, AcceptedSnapshotId = "accepted-" + Uploads, NextLineageToken = "lineage" }));
        }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "new"; break; case "colony": Ports.Colony = "new"; break;
                case "auth": Ports.Auth = "new"; break; case "server": Ports.Server = "http://new"; break;
                case "snapshot": Ports.Snapshot = "new"; break; case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth }); break;
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); break; case "reset": State.Reset(); Incidents.Reset(); break;
            }
        }
        public string Idle => "idle";
        public string Busy => "busy";
        public string BaseSnapshotChanged => "base-changed";
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public string Text(string key, params (string Name, object? Value)[] args) => key + string.Join("", args.Select(a => ":" + a.Name + "=" + a.Value));
        public string Summary(ModBankStatusResponseDto? status) => "summary";
        public void Message(string message, bool rejected) { }
        public void IncidentReported(string message) => Notices++;
        public void Diagnose(Exception exception) { }
        public ModSnapshotUploadResult Result(ClientSnapshotUploadResult result) => result.Failure is { } failure
            ? ModSnapshotUploadResult.Failed(failure.Code, failure.Message ?? "fallback") : ModSnapshotUploadResult.Ok(result.AcceptedSnapshotId!, result.SourcePath!);
        public string Failed(string status, string? code, string? reason) => "failure:" + code;
        public string Succeeded(string? id) => "success:" + id;
        public string Exception(string status, Exception ex) => "exception:" + ex.Message;
        public void DefaultStatus(string message) { }
        public void Message(string message, bool accepted, bool bestEffort) { }
        public void Diagnose(LocalMutationKind operation, string message, Exception? exception) { }
        public void Dispose() { Incidents.Reset(); Sync.Reset(); ClientSessionWork.Reset(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
