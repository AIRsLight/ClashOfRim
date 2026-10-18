using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;

internal static class ClientAdministrationTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        int count = 0; var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string operation in new[] { "read", "save", "action" })
                {
                    await Success(operation); count++;
                    foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "permission", "unavailable", "game", "snapshot" })
                        foreach (string phase in new[] { "start", "send", "queue", "success-text" }.Concat(operation == "read" ? new[] { "enforce" } : new[] { "notice" }))
                        { await Ownership(operation, change, phase); count++; }
                    foreach (string fault in new[] { "network", "null", "missing-result", "rejected", "exception", "canceled" })
                    { await Failure(operation, fault); count++; }
                    foreach (bool oldFirst in new[] { false, true })
                        foreach (bool fails in new[] { false, true }) { await Replacement(operation, oldFirst, fails); count++; }
                    foreach (string phase in new[] { "success-text", operation == "read" ? "enforce" : "notice" })
                    { await CallbackFailure(operation, phase); count++; }
                    await Queued(operation); count++; await Busy(operation); count++;
                }
                await Cache(); count++;
                foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "permission" }) { await CacheLifetime(change); count++; }
                foreach (string missing in new[] { "user", "server", "colony", "auth", "permission", "unavailable" }) { await Denial(missing); count++; }
                await Grant(); count++; await RevokedResponse(); count++; await ActionRefreshFailure(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: administrator control ({count} scenarios)");
    }
    private static async Task Success(string operation)
    {
        var f = new Fixture(); var task = f.Start(operation); var queued = await f.Ports.Next();
        Check(f.Access.Busy && f.Notices.Count == 0 && f.Module.Snapshot is null, "Result published before game queue.");
        queued(); queued(); await f.Ports.PumpUntil(task);
        Check(!f.Access.Busy && f.Diagnostics.Count == 0 && f.Notices.Count == (operation == "read" ? 0 : 1), "Completion/notice not exact.");
        Check(f.Calls.Select(c => c.Operation).SequenceEqual(operation == "action" ? new[] { "action", "read" } : new[] { operation }), "Wrong command/refresh order.");
        Check(f.Calls.All(c => c.Context.UserId == "alice" && c.Context.ColonyId == "colony" && c.Context.CurrentSnapshotId == "base"), "Lost captured identity.");
        Check(f.Module.Message.Contains(operation == "save" ? "StatusSaved" : "StatusRefreshed"), "Wrong final status.");
        if (operation == "action") Check(f.Notices.Single() == "ClashOfRim.Admin.StatusActionDone", "Action notice was replaced by refresh status.");
    }
    private static async Task Ownership(string operation, string change, string phase)
    {
        var f = new Fixture(); bool changed = false;
        f.Hook = point => { if (point == phase && !changed) { changed = true; f.Change(change); } };
        await f.Ports.PumpUntil(f.Start(operation));
        Check(changed, "Missed phase " + operation + "/" + phase);
        bool allowed = change is "game" or "snapshot";
        Check(f.Notices.Count == (operation != "read" && (allowed || phase == "notice") ? 1 : 0), $"Stale notice {operation}/{change}/{phase}");
        Check(f.Diagnostics.Count == 0 && !f.Access.Busy, "Stale failure/unreleased admission.");
        if (!allowed)
        {
            var trace = f.Trace.ToArray(); int at = Array.IndexOf(trace, phase);
            Check(at == trace.Length - 1, $"Old stages continued {operation}/{change}/{phase}: {string.Join(",", trace.Skip(at))}");
        }
        if (change == "snapshot" && operation == "action") Check(f.Calls.Last().Context.CurrentSnapshotId == "new", "Follow-up did not capture latest snapshot.");
    }
    private static async Task Failure(string operation, string fault)
    {
        var f = new Fixture { Fault = fault, FaultOperation = operation };
        await f.Ports.PumpUntil(f.Start(operation));
        string expected = fault is "exception" or "canceled" ? "StatusException" : fault is "missing-result" or "rejected" ? "StatusRejected" : "StatusFailed";
        Check(f.Module.Message.Contains(expected) && f.Module.Snapshot is null && f.Notices.Count == 0 && f.Calls.Count == 1 && !f.Access.Busy, "Failure published/followed up: " + operation + "/" + fault);
        Check(f.Access.IsAdministrator, "Unaccepted response changed authority.");
        f.Fault = null; await f.Ports.PumpUntil(f.Start(operation));
        Check(f.Module.Message.Contains(operation == "save" ? "StatusSaved" : "StatusRefreshed"), "Failure prevented retry.");
    }
    private static async Task CallbackFailure(string operation, string phase)
    {
        var f = new Fixture(); bool thrown = false;
        f.Hook = point => { if (point == phase && !thrown) { thrown = true; throw new InvalidOperationException("expected"); } };
        await f.Ports.PumpUntil(f.Start(operation), _ => { });
        Check(thrown && f.Module.Message.Contains("StatusException") && f.Diagnostics.Count == 1 && !f.Access.Busy, "Callback failure lost: " + phase);
        f.Hook = null; await f.Ports.PumpUntil(f.Start(operation)); Check(f.Module.Message.Contains(operation == "save" ? "StatusSaved" : "StatusRefreshed"), "Callback exception prevented retry.");
    }
    private static async Task Replacement(string operation, bool oldFirst, bool fails)
    {
        var f = new Fixture(); var entered = Signal(); var release = Signal();
        f.Before = async _ => { entered.TrySetResult(); await release.Task; };
        if (fails) { f.FaultOperation = operation; f.Fault = "exception"; }
        var old = f.Start(operation); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Access.SetAdministrator(false); f.Access.SetAdministrator(true); f.Fault = null; f.Before = null;
        var next = f.Start("read"); var queued = await f.Ports.Next();
        if (oldFirst) { release.SetResult(); await old; Check(f.Access.Busy, "Old completion released new admission."); }
        queued(); await next;
        if (!oldFirst) { release.SetResult(); await old; }
        Check(f.Module.Snapshot is not null && f.Notices.Count == 0 && f.Diagnostics.Count == 0 && !f.Access.Busy, "Old completion crossed revocation/regrant.");
    }
    private static async Task Queued(string operation)
    {
        var f = new Fixture(); var old = f.Start(operation); var oldQueue = await f.Ports.Next();
        f.Change("login"); var next = f.Start("read"); var queue = await f.Ports.Next();
        oldQueue(); oldQueue(); await old; Check(f.Access.Busy && f.Module.Snapshot is null && f.Notices.Count == 0, "Old queue crossed login/released next.");
        queue(); await next; f.Discard = true; int calls = f.Calls.Count;
        await f.Ports.PumpUntil(f.Start(operation));
        Check(f.Calls.Count == calls + 1 && f.Notices.Count == 0 && !f.Access.Busy, "Discarded action followed up/retained admission.");
    }
    private static async Task Busy(string operation)
    {
        var f = new Fixture(); var held = f.Access.Begin(out _)!;
        await f.Start(operation); Check(f.Calls.IsEmpty && f.Trace.IsEmpty && f.Access.Busy, "Bypassed world write/other admin admission.");
        f.Access.End(held);
        var task = f.Start(operation); var queue = await f.Ports.Next(); await f.Start("save");
        Check(f.Calls.Count == 1 && f.Access.Begin(out _) is null, "Queued work released admission too early.");
        queue(); await f.Ports.PumpUntil(task);
    }
    private static async Task Cache()
    {
        var f = new Fixture(); var read = f.Module.RefreshAsync(); var queue = await f.Ports.Next();
        f.ReadResponse.Configuration!.TradeOrderExpirationDays = 999; f.ReadResponse.Players[0].UserId = "mutated";
        queue(); await read;
        var previous = f.Module.Snapshot!;
        Check(previous.Configuration!.TradeOrderExpirationDays == 3 && previous.Players[0].UserId == "alice", "Read response not frozen.");
        var edit = Config(7); var save = f.Module.SaveAsync(edit); edit.CompatibilityMods[0].Configs[0].Mode = "mutated"; edit.TradeOrderExpirationDays = 88;
        var saveQueue = await f.Ports.Next(); f.SaveResponse.Configuration!.TradeOrderExpirationDays = 999;
        saveQueue(); await save;
        Check(f.Sent!.TradeOrderExpirationDays == 7 && f.Sent.CompatibilityMods[0].Configs[0].Mode == "Enforce", "Caller edit leaked into background request.");
        Check(f.Module.Snapshot!.Configuration!.TradeOrderExpirationDays == 5 && previous.Configuration.TradeOrderExpirationDays == 3
            && !ReferenceEquals(previous, f.Module.Snapshot), "Save mutated prior published view/retained response graph.");
        f.SaveResponse.Configuration = null; await f.Ports.PumpUntil(f.Module.SaveAsync(Config(9)));
        Check(f.Module.Snapshot!.Configuration!.TradeOrderExpirationDays == 5, "Absent accepted config overwrote prior cache.");
        f.Module.Message = "world write"; Check(f.Module.Message == "world write", "Shared world-write status lost.");
        f.Module.Reset(); Check(f.Module.Snapshot is null && f.Module.Message.Contains("StatusIdle"), "Reset did not clear module state.");
        await f.Ports.PumpUntil(f.Module.SaveAsync(Config(10))); Check(f.Module.Snapshot is null, "Save fabricated a full status cache.");
    }
    private static async Task CacheLifetime(string change)
    {
        var f = new Fixture(); await f.Ports.PumpUntil(f.Module.RefreshAsync()); f.Change(change);
        Check(f.Module.Snapshot is null, "Prior authority's cache survived " + change);
        f.Access.SetAdministrator(true); Check(f.Module.Snapshot is null, "Regrant revived old cache.");
    }
    private static async Task Denial(string missing)
    {
        var f = new Fixture();
        switch (missing) { case "user": f.Ports.User = ""; break; case "server": f.Ports.Server = ""; break; case "colony": f.Ports.Colony = ""; break; case "auth": f.Ports.Auth = ""; break; default: f.Change(missing); break; }
        await f.Module.RefreshAsync();
        Check(f.Calls.IsEmpty && !f.Access.Busy && f.Module.Message.Contains(missing == "permission" ? "NotAdministrator" : "NotConnected"), "Wrong denial: " + missing);
    }
    private static async Task Grant()
    {
        var f = new Fixture(); f.Access.SetAdministrator(false);
        await f.Ports.PumpUntil(f.Module.ObservePermission(true, refresh: true));
        await f.Module.ObservePermission(true, refresh: true);
        Check(f.Calls.Count == 1 && f.Access.IsAdministrator && f.Module.Snapshot is not null, "Repeated role observation refreshed again.");
    }
    private static async Task RevokedResponse()
    {
        var f = new Fixture(); await f.Ports.PumpUntil(f.Module.RefreshAsync());
        f.ReadResponse.IsAdministrator = false; await f.Ports.PumpUntil(f.Module.RefreshAsync());
        Check(!f.Access.IsAdministrator && f.Module.Snapshot is null && !f.Access.Busy, "Accepted revocation did not clear state/admission.");
    }
    private static async Task ActionRefreshFailure()
    {
        var f = new Fixture { FaultOperation = "read", Fault = "network" };
        f.ActionResponse.Result!.Message = "已执行 İ العربية";
        await f.Ports.PumpUntil(f.Start("action"));
        Check(f.Notices.Single() == "已执行 İ العربية" && f.Module.Message.Contains("StatusFailed") && !f.Access.Busy, "Refresh failure rewrote action outcome or kept admission.");
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static ModAdminConfigurationDto Config(int days) => new() { TradeOrderExpirationDays = days, TradeBaseFeeRate = 0.13f,
        CompatibilityMods = new() { new() { PackageId = "测试 İ", Configs = new() { new() { FileName = "settings.xml", Mode = "Enforce" } } } } };
    private sealed class Fixture : IClientAdminTransport, IClientAdminPresentation
    {
        internal readonly EventModuleTestPorts Ports = new(); internal readonly ClientSessionLifecycle Session;
        internal readonly ClientAdminAccess Access; internal readonly ClientAdministration Module;
        internal readonly ConcurrentQueue<string> Trace = new(), Notices = new(), Diagnostics = new();
        internal readonly ConcurrentQueue<(string Operation, ClashOfRimClientNetworkContext Context)> Calls = new();
        internal Action<string>? Hook; internal Func<string, Task>? Before; internal string? FaultOperation, Fault; internal bool Discard;
        internal ModAdminConfigurationDto? Sent;
        internal ModAdminStatusResponseDto ReadResponse = new() { Result = new() { Accepted = true }, IsAdministrator = true, Configuration = Config(3), Players = new() { new() { UserId = "alice" } } };
        internal ModAdminUpdateConfigurationResponseDto SaveResponse = new() { Result = new() { Accepted = true }, Configuration = Config(5) };
        internal ModAdminActionResponseDto ActionResponse = new() { Result = new() { Accepted = true } };
        internal Fixture()
        { ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports)); Access = new(Session); Module = new(Access, this, this); Access.SetAdministrator(true); }
        internal Task Start(string operation) => operation switch { "read" => Module.RefreshAsync(), "save" => Module.SaveAsync(Config(7)), _ => Module.ActAsync(new("Custom İ", "target", "colony", "消息", "Critical", false)) };
        internal void Step(string phase) { Trace.Enqueue(phase); Hook?.Invoke(phase); }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "game": Ports.GameOwner = new(); break; case "snapshot": Ports.Snapshot = "new"; break;
                case "permission": Access.SetAdministrator(false); break; case "generation": ClientSessionWork.Reset(); break;
                case "unavailable": Session.BeginUnavailable(Ports.Auth, null); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new" }); Access.SetAdministrator(true); break;
            }
        }
        public string Text(string key, params (string Name, object? Value)[] args)
        {
            if (key is "ClashOfRim.Admin.StatusRefreshing" or "ClashOfRim.Admin.StatusSaving" or "ClashOfRim.Admin.StatusSendingAction") Step("start");
            if (key is "ClashOfRim.Admin.StatusRefreshed" or "ClashOfRim.Admin.StatusSaved" or "ClashOfRim.Admin.StatusActionDone") Step("success-text");
            return key + (args.Length == 0 ? "" : " " + string.Join(";", args.Select(a => a.Value)));
        }
        public void Success(string message) { Notices.Enqueue(message); Step("notice"); }
        public void Diagnose(string message) => Diagnostics.Enqueue(message);
        public void EnforcePermission() => Step("enforce");
        public void Enqueue(Action action, Action discard) { Step("queue"); Ports.Enqueue(() => { if (Discard) discard(); else action(); }); }
        public Task<ClashOfRimClientNetworkResult<ModAdminStatusResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
            => Network("read", context, ReadResponse, result => ReadResponse.Result = result);
        public Task<ClashOfRimClientNetworkResult<ModAdminUpdateConfigurationResponseDto>> SaveAsync(ClashOfRimClientNetworkContext context, ModAdminConfigurationDto configuration, CancellationToken token)
        { Sent = configuration; return Network("save", context, SaveResponse, result => SaveResponse.Result = result); }
        public Task<ClashOfRimClientNetworkResult<ModAdminActionResponseDto>> ActAsync(ClashOfRimClientNetworkContext context, ClientAdminCommand command, CancellationToken token)
        { Check(command.Kind == "Custom İ" && command.Message == "消息" && !command.Persistent, "Command fields changed."); return Network("action", context, ActionResponse, result => ActionResponse.Result = result); }
        private async Task<ClashOfRimClientNetworkResult<T>> Network<T>(string operation, ClashOfRimClientNetworkContext context, T response, Action<ModProtocolResponseDto?> setResult) where T : class
        {
            Calls.Enqueue((operation, context)); string? fault = operation == FaultOperation ? Fault : null;
            Step("send"); if (Before is not null) await Before(operation);
            switch (fault)
            {
                case "network": return ClashOfRimClientNetworkResult<T>.Failed("network", "expected");
                case "null": return ClashOfRimClientNetworkResult<T>.Ok(null!);
                case "missing-result": setResult(null); break;
                case "rejected": setResult(new() { Accepted = false, ErrorCode = 17, Message = "expected" }); break;
                case "exception": throw new InvalidOperationException("expected");
                case "canceled": throw new TaskCanceledException("expected");
                default: setResult(new() { Accepted = true, Message = operation == "action" ? ActionResponse.Result?.Message : null }); break;
            }
            return ClashOfRimClientNetworkResult<T>.Ok(response);
        }
    }
}
