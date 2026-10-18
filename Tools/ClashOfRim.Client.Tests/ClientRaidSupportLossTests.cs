using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientRaidSupportLossTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (int count in new[] { 0, 1, 3 })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                using var f = new Fixture(count);
                var batch = f.Capture(); f.Input.Clear();
                Task<ClientRaidSupportLossResult> work = f.Module.ResolveAsync(batch);
                (await f.Ports.Next())(); var result = await work;
                Check(result.Success && result.Failure is null && f.Sent.Count == count && f.Removed.Count == count
                    && f.Attempt.Admission.IsCurrent && f.Ports.Captures == 0, "Loss prerequisite consumed admission, built a snapshot or lost captured batch.");
                Check(f.Trace.SequenceEqual(Enumerable.Range(0, count).Select(i => "mark:" + i)
                    .Concat(Enumerable.Range(0, count).Select(i => "send:" + i)).Concat(Enumerable.Range(0, count).Select(i => "remove:" + i))), "Local deletion preceded all receipts.");
                Check(f.Sent.All(command => command.IdempotencyKey == $"support-finish:alice:colony:{command.Payload.EventId}:RaidSettlementLost"
                    && command.Payload is { Dead: true, Package: null, FinishReason: "RaidSettlementLost" }), "Loss command diverged from support return protocol.");
            }
            foreach (string failure in new[] { "network", "permanent", "reject", "missing-result", "missing-receipt", "throw" })
            {
                using var f = new Fixture(3) { Fault = failure, FaultAt = 1 };
                var batch = f.Capture(); Task<ClientRaidSupportLossResult> work = f.Module.ResolveAsync(batch);
                (await f.Ports.Next())(); var result = await work;
                Check(!result.Success && result.Failure is not null && result.Failure.CanRetry == (failure == "network")
                    && f.Sent.Count == 2 && f.Removed.Count == 0 && f.Pending.Count == 0 && f.Attempt.Admission.IsCurrent,
                    "Failure removed pawns, continued reporting, or stranded flags: " + failure);
                if (failure == "network")
                {
                    string[] original = f.Sent.Select(c => c.IdempotencyKey).ToArray(); f.Fault = ""; f.Sent.Clear(); f.Input.Clear();
                    f.Attempt.Admission.Dispose(); Check(f.Sync.TryBeginManual(out var retry), "No retry admission.");
                    batch = batch.WithAdmission(retry!);
                    work = f.Module.ResolveAsync(batch); (await f.Ports.Next())(); result = await work;
                    Check(result.Success && f.Sent.Count == 3 && f.Sent.Take(2).Select(c => c.IdempotencyKey).SequenceEqual(original), "Replay changed previously accepted loss identities or rescanned source.");
                    retry!.Dispose();
                }
            }
            foreach (bool throws in new[] { false, true })
            {
                using var f = new Fixture(3) { Fault = throws ? "remove-throw" : "remove-false", FaultAt = 1 };
                var work = f.Module.ResolveAsync(f.Capture());
                try { (await f.Ports.Next())(); } catch (InvalidOperationException) when (throws) { }
                (await f.Ports.Next())(); var result = await work;
                Check(!result.Success && result.Failure is { CanRetry: false } && f.Removed.SequenceEqual(new[] { "0" })
                    && f.Pending.Count == 0, "Partial removal replayable or lost cleanup.");
            }
            foreach (string change in new[] { "server", "auth", "user", "colony", "snapshot", "game", "login", "generation" })
            foreach (string stage in new[] { "capture", "http", "publication", "failure-cleanup" })
            {
                using var f = new Fixture(3);
                if (stage == "capture") f.AfterCapture = () => Change(f, change);
                if (stage == "http") f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (stage == "failure-cleanup") { f.Fault = "network"; f.FaultAt = 0; }
                var batch = f.Module.Capture(f.Attempt, f);
                if (stage == "capture") { Check(batch is null && f.Sent.Count == 0, "Capture rebound to another owner."); continue; }
                Task<ClientRaidSupportLossResult> work = f.Module.ResolveAsync(batch!);
                if (stage == "http")
                {
                    await f.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5)); Change(f, change);
                    f.Hold!.SetResult(Ok());
                }
                else { var callback = await f.Ports.Next(); Change(f, change); callback(); }
                try { await work.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) when (change == "generation") { }
                Check(f.Removed.Count == 0 && f.Pending.Count == 3, "Late loss modified another game's pawns or flags: " + stage + "/" + change);
                if (stage == "http") Check(f.Sent.Count == 1, "Stale response sent another loss.");
            }
            using var replaced = new Fixture(1) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var pending = replaced.Module.ResolveAsync(replaced.Capture()); await replaced.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            replaced.Sync.Reset(); Check(replaced.Sync.TryBeginManual(out var next), "Could not replace admission.");
            replaced.Hold!.SetResult(Ok()); await pending;
            Check(next!.IsCurrent && replaced.Removed.Count == 0, "Old batch affected replacement admission."); next.Dispose();
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private static ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto> Ok() =>
        ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>.Ok(new() { Result = new() { Accepted = true }, NotificationEventId = "notification" });
    private static void Change(Fixture f, string kind)
    {
        switch (kind)
        {
            case "server": f.Ports.Server = "http://new"; break;
            case "auth": f.Ports.Auth = "new"; break;
            case "user": f.Ports.User = "new"; break;
            case "colony": f.Ports.Colony = "new"; break;
            case "snapshot": f.Ports.Snapshot = "new"; break;
            case "game": f.Ports.GameOwner = new(); break;
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "new" }); break;
            case "generation": ClientSessionWork.Reset(); break;
        }
    }
    private sealed class Fixture : IClientRaidSupportLossSource, IClientSupportReturnTransport, IDisposable
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSnapshotTransaction.Attempt Attempt;
        internal readonly ClientRaidSupportLosses Module;
        internal readonly List<ClientRaidSupportLoss> Input;
        private readonly int capturedCount;
        internal readonly List<ClientSupportReturnCommand> Sent = new();
        internal readonly List<string> Removed = new(), Trace = new();
        internal readonly HashSet<string> Pending = new();
        internal string Fault = "";
        internal int FaultAt;
        internal Action? AfterCapture;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>>? Hold;
        internal readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture(int count)
        {
            capturedCount = count;
            ClientSessionWork.Reset(); Input = Enumerable.Range(0, count).Select(i => new ClientRaidSupportLoss(i.ToString(CultureInfo.InvariantCulture), "global-" + i, "支援-" + i)).ToList();
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports, Ports));
            Check(Sync.TryBeginManual(out var lease), "No manual admission."); Attempt = transactions.Capture(lease!); Module = new(transactions, this);
        }
        internal ClientRaidSupportLosses.Batch Capture() => Module.Capture(Attempt, this)!;
        public IReadOnlyList<ClientRaidSupportLoss> CaptureSource() { AfterCapture?.Invoke(); return Input; }
        IReadOnlyList<ClientRaidSupportLoss> IClientRaidSupportLossSource.Capture() => CaptureSource();
        public void InProgress(ClientRaidSupportLoss loss, bool value)
        { if (value) Pending.Add(loss.EventId); else Pending.Remove(loss.EventId); Trace.Add((value ? "mark:" : "reset:") + loss.EventId); }
        public bool RemoveAndComplete(ClientRaidSupportLoss loss)
        {
            Check(Sent.Count == capturedCount, "Removed before completing batch reports.");
            if (loss.EventId == FaultAt.ToString(CultureInfo.InvariantCulture))
            { if (Fault == "remove-throw") throw new InvalidOperationException("partial removal"); if (Fault == "remove-false") return false; }
            Removed.Add(loss.EventId); Pending.Remove(loss.EventId); Trace.Add("remove:" + loss.EventId); return true;
        }
        public Task<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>> FinishAsync(ClashOfRimClientNetworkContext context,
            ClientSupportReturnCommand command, CancellationToken token)
        {
            Check(ReferenceEquals(context, Attempt.Context) && token == Attempt.Owner.Token && Removed.Count == 0, "Loss changed identity/token or removed before reporting.");
            Sent.Add(command); Trace.Add("send:" + command.Payload.EventId); Submitted.TrySetResult();
            if (Hold is not null) return Hold.Task;
            if (command.Payload.EventId != FaultAt.ToString(CultureInfo.InvariantCulture)) return Task.FromResult(Ok());
            if (Fault == "throw") throw new InvalidOperationException("transport fixture");
            var result = Fault switch
            {
                "network" => ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>.Failed("HttpRequestException", "offline"),
                "permanent" => ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>.Failed("HttpError", "400 Bad Request"),
                "reject" => ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>.Ok(new() { Result = new() { Accepted = false } }),
                "missing-result" => ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>.Ok(new() { NotificationEventId = "notification" }),
                "missing-receipt" => ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>.Ok(new() { Result = new() { Accepted = true } }),
                _ => Ok()
            };
            return Task.FromResult(result);
        }
        public void Dispose() => Attempt.Admission.Dispose();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
