using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Account;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Accounts;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class PasswordChangeModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Protocol();
                foreach (bool game in new[] { true, false }) await Success(game);
            }
            await Admission(); await Failures(); await Stale(); await Reentry(); await Replacement(); await Cancellation();
            StoreAndDispatcher();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Protocol()
    {
        var context = new ClashOfRimClientNetworkContext("http://localhost/base", "用户-é", "colony", "snapshot", null, "unused", "auth");
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/base/" + ProtocolContractManifest.Find(ProtocolMessageKind.ChangeOfflinePassword).Route.TrimStart('/'), "Password route changed.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var json = body.RootElement;
            Check(json.EnumerateObject().Count() == 5 && json.GetProperty("userId").GetString() == context.UserId
                && json.GetProperty("colonyId").GetString() == "colony" && json.GetProperty("authToken").GetString() == "auth"
                && json.GetProperty("currentPassword").GetString() == " old-é " && json.GetProperty("newPassword").ValueKind == JsonValueKind.Null,
                "Password protocol changed credentials, whitespace, null semantics or wire fields.");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true,\"message\":\"ok\"}}", Encoding.UTF8, "application/json") };
        }));
        var protocol = new ClientAccountProtocol(new ClientProtocolTransport(http, context, new ClientProtocolEnvironment(static (_, _) => { })), () => "unconfigured");
        Check((await protocol.ChangeAsync(context, " old-é ", null, default)).Response!.Result!.Accepted, "Password protocol lost receipt.");
        Check((await new ClashOfRimModNetworkClient(http, context).ChangeOfflinePasswordAsync(" old-é ", null)).Success && calls == 2, "Public password facade did not forward.");
        Check(!(await protocol.ChangeAsync(new("", "", "", null, null, null, null), "", "", default)).Success && calls == 2, "Unconfigured protocol reached HTTP.");
    }
    private static async Task Success(bool game)
    {
        var f = new Fixture(); f.Ports.HasGame = game; f.Ports.Snapshot = "";
        var work = f.Module.StartAsync(" old-é ", " new-文化 "); var publish = await f.Ports.Next();
        Check(f.Module.Busy && f.Password == "old" && f.Persisted == 0, "Password was written on the worker or request completed before publication.");
        await f.Module.StartAsync("duplicate", "duplicate");
        Check(f.Calls == 1 && f.Context!.CurrentSnapshotId == "" && f.CurrentPassword == " old-é " && f.NewPassword == " new-文化 ", "Password command was not frozen or busy request sent twice.");
        f.Ports.GameOwner = new(); f.Ports.HasGame = !game; f.Ports.Snapshot = "advanced";
        f.Result.Response!.Result!.Accepted = false; f.Result.Response.Result.Message = "mutated response";
        publish(); await work;
        Check(f.Password == " new-文化 " && f.Persisted == 1 && !f.Module.Busy && f.Module.Message == "Changed:server-文化", "Game/snapshot transition lost the password or mutable receipt escaped publication.");
        Check(f.Trace.SequenceEqual(new[] { "write", "persist", "report:Changed" }) && f.Ports.Captures == 0 && f.Ports.Published == 0, "Password publication order changed or touched snapshots.");
        f.Result = Ok(); await f.Ports.PumpUntil(f.Module.StartAsync(" new-文化 ", null));
        Check(f.Password == "" && f.Persisted == 2, "Empty-password compatibility changed.");
    }
    private static async Task Admission()
    {
        foreach (var condition in new[] { "server", "user", "colony", "auth", "unavailable" })
        {
            var f = new Fixture();
            switch (condition)
            {
                case "server": f.Ports.Server = ""; break;
                case "user": f.Ports.User = ""; break;
                case "colony": f.Ports.Colony = ""; break;
                case "auth": f.Ports.Auth = ""; break;
                default: f.Change(condition); break;
            }
            await f.Module.StartAsync("old", "new");
            Check(f.Calls == 0 && !f.Module.Busy && f.Module.Message.StartsWith("NoSession"), "Invalid session started password work: " + condition);
        }
        var replaced = new Fixture(); replaced.OnFormat = n => { if (n.Stage == ClientPasswordStage.Changing) replaced.Change("login"); };
        await replaced.Module.StartAsync("old", "new");
        Check(replaced.Calls == 0 && !replaced.Module.Busy, "Identity changed before dispatch but old request was sent.");
    }
    private static async Task Failures()
    {
        foreach (var kind in new[] { "transport", "missing", "rejected", "exception", "cancel", "persist" })
        {
            var f = new Fixture();
            switch (kind)
            {
                case "transport": f.Result = ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>.Failed("offline", "network"); break;
                case "missing": f.Result = ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>.Ok(new()); break;
                case "rejected": f.Result.Response!.Result = new() { Accepted = false, ErrorCode = 7, Message = "rejected" }; break;
                case "exception": f.Send = _ => throw new InvalidOperationException("send failed"); break;
                case "cancel": f.Send = _ => throw new OperationCanceledException(); break;
                case "persist": f.OnPersist = () => throw new InvalidOperationException("disk failed"); break;
            }
            await f.Ports.PumpUntil(f.Module.StartAsync("old", "new"), _ => { });
            Check(!f.Module.Busy && f.Calls == 1 && f.Notices.Count(n => n.Stage == ClientPasswordStage.Changed) == 0, "Failure succeeded, stuck busy or retried automatically: " + kind);
            Check(f.Password == (kind == "persist" ? "new" : "old") && f.Module.Message.StartsWith(kind == "persist" ? "SaveFailed" : kind == "rejected" ? "Rejected" : kind is "exception" or "cancel" ? "Exception" : "Failed"), "Failure lost credential or stage: " + kind);
            f.Result = Ok(); f.Send = null; f.OnPersist = null;
            await f.Ports.PumpUntil(f.Module.StartAsync("new", "retry"));
            Check(f.Password == "retry" && !f.Module.Busy && f.Calls == 2, "Failure prevented explicit later request.");
        }
    }
    private static async Task Stale()
    {
        foreach (var change in Changes)
        foreach (bool queued in new[] { false, true })
        foreach (bool fail in new[] { false, true })
        {
            var f = new Fixture(); var reply = new TaskCompletionSource<ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Send = _ => reply.Task; var work = f.Module.StartAsync("old", "stale"); await f.Sent.Task;
            var result = fail ? ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>.Failed("offline", "network") : Ok();
            Action? pending = null;
            if (queued) { reply.SetResult(result); pending = await f.Ports.Next(); }
            f.Change(change);
            if (!queued) reply.SetResult(result); else pending!();
            await f.Ports.PumpUntil(work);
            Check(f.Password == "old" && f.Persisted == 0 && f.Trace.Count == 0 && !f.Module.Busy, "Stale password published: " + change + "/" + queued + "/" + fail);
        }
    }
    private static async Task Reentry()
    {
        foreach (var change in Changes)
        foreach (var phase in new[] { "write", "persist", "format" })
        {
            var f = new Fixture();
            Action changeOwner = () => f.Change(change);
            if (phase == "write") f.OnWrite = changeOwner;
            if (phase == "persist") f.OnPersist = changeOwner;
            if (phase == "format") f.OnFormat = n => { if (n.Stage == ClientPasswordStage.Changed) changeOwner(); };
            await f.Ports.PumpUntil(f.Module.StartAsync("old", "new"));
            Check(f.Persisted == (phase == "write" ? 0 : 1) && !f.Notices.Any(n => n.Stage == ClientPasswordStage.Changed) && !f.Module.Busy,
                "Reentrant identity change published later effects: " + change + "/" + phase);
        }
    }
    private static async Task Replacement()
    {
        foreach (bool queued in new[] { false, true })
        {
            var f = new Fixture(); var oldReply = new TaskCompletionSource<ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Send = _ => oldReply.Task; var oldWork = f.Module.StartAsync("old", "stale"); await f.Sent.Task; Action? oldPublish = null;
            if (queued) { oldReply.SetResult(Ok()); oldPublish = await f.Ports.Next(); }
            f.Change("login");
            var newReply = new TaskCompletionSource<ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Send = _ => newReply.Task; var newWork = f.Module.StartAsync("old", "current");
            if (queued) oldPublish!(); else oldReply.SetResult(Ok());
            await oldWork;
            Check(f.Module.Busy && f.Password == "old", "Old finally cleared replacement request or wrote its password.");
            newReply.SetResult(Ok()); await f.Ports.PumpUntil(newWork);
            Check(f.Password == "current" && f.Persisted == 1 && !f.Module.Busy, "Replacement did not retain ownership.");
        }
    }
    private static async Task Cancellation()
    {
        foreach (bool queued in new[] { false, true })
        {
            var f = new Fixture();
            if (!queued) f.Send = async token => { await Task.Delay(Timeout.Infinite, token); return Ok(); };
            var work = f.Module.StartAsync("old", "stale"); Action? pending = null;
            if (queued) pending = await f.Ports.Next(); else await f.Sent.Task;
            f.Change("generation"); await work.WaitAsync(TimeSpan.FromSeconds(5)); pending?.Invoke();
            Check(f.Password == "old" && !f.Module.Busy && f.Persisted == 0, "Canceled password work remained active.");
        }
    }
    private static void StoreAndDispatcher()
    {
        var settings = new ClashOfRimSettings { UserId = "user", AuthToken = "auth", OfflinePassword = "old" };
        new ClientPasswordStore(settings).Write(" new ");
        Check(settings.UserId == "user" && settings.AuthToken == "auth" && settings.OfflinePassword == " new ", "Narrow store changed identity.");
        var previous = Verse.Current.Game;
        try
        {
            int applied = 0, discarded = 0;
            Verse.Current.Game = (Verse.Game)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Verse.Game));
            ClientMainThreadDispatcher.EnqueueSession(() => applied++, () => discarded++);
            Verse.Current.Game = (Verse.Game)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Verse.Game));
            AIRsLight.ClashOfRim.MainMenu.ClashOfRimMainMenuPatches.RunQueuedMainThreadActions();
            Check(applied == 1 && discarded == 0, "Session UI work was tied to a Game.");
            ClientMainThreadDispatcher.EnqueueSession(() => applied++, () => discarded++); ClientSessionWork.Reset();
            AIRsLight.ClashOfRim.MainMenu.ClashOfRimMainMenuPatches.RunQueuedMainThreadActions();
            Check(applied == 1 && discarded == 1, "Session dispatcher crossed exit.");
        }
        finally { Verse.Current.Game = previous; }
    }
    private static readonly string[] Changes = { "user", "auth", "server", "colony", "login", "clear", "unavailable", "generation" };
    private static ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto> Ok() => ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>.Ok(new() { Result = new() { Accepted = true, Message = "server-文化" } });
    private sealed class Fixture : IClientPasswordTransport, IClientPasswordStore, IClientPasswordPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientPasswordChange Module;
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Trace = new();
        internal readonly List<ClientPasswordNotice> Notices = new();
        internal ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto> Result = Ok();
        internal Func<CancellationToken, Task<ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>>>? Send;
        internal Action? OnWrite, OnPersist;
        internal Action<ClientPasswordNotice>? OnFormat;
        internal ClashOfRimClientNetworkContext? Context;
        internal string? CurrentPassword, NewPassword;
        internal string Password = "old";
        internal int Calls, Persisted;
        private bool onMain;
        internal Fixture()
        {
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            Module = new(Session, this, this, this, (action, discard) => Ports.Enqueue(ClientSessionWork.Guard(() =>
            { onMain = true; try { action(); } finally { onMain = false; } }, discard)));
        }
        public async Task<ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>> ChangeAsync(ClashOfRimClientNetworkContext context, string? currentPassword, string? newPassword, CancellationToken token)
        { Calls++; Context = context; CurrentPassword = currentPassword; NewPassword = newPassword; var send = Send; Sent.TrySetResult(); return send is null ? Result : await send(token); }
        public void Write(string password) { Check(onMain, "Password write was not on main thread."); Trace.Add("write"); Password = password; OnWrite?.Invoke(); }
        public void Persist() { Check(onMain, "Password persistence was not on main thread."); Trace.Add("persist"); Persisted++; OnPersist?.Invoke(); }
        public string Format(ClientPasswordNotice notice) { OnFormat?.Invoke(notice); return notice.Stage + ":" + notice.Message; }
        public void Report(ClientPasswordNotice notice, string message)
        {
            Notices.Add(notice);
            if (notice.Stage is ClientPasswordStage.Changing or ClientPasswordStage.NoSession) return;
            Check(onMain, "Password notification was not on main thread."); Trace.Add("report:" + notice.Stage);
        }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break;
                case "clear": Session.ClearIdentity(); break;
                case "unavailable": Session.BeginUnavailable("auth", null); break;
                case "generation": ClientSessionWork.Reset(); break;
            }
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
