using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Achievements;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientAchievementQueryTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        int count = 0; var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                await Protocol(); count++;
                foreach (bool hasGame in new[] { false, true }) { await Cache(hasGame); count++; }
                foreach (bool finishOldFirst in new[] { false, true })
                    foreach (string outcome in new[] { "success", "rejected", "exception" }) { await Replacement(finishOldFirst, outcome); count++; }
                foreach (string change in new[] { "user", "colony", "server", "auth", "game", "generation", "login", "reset" })
                {
                    foreach (string phase in new[] { "cache", "send", "publication" }) { await Ownership(change, phase); count++; }
                }
                foreach (string mode in new[] { "network", "null-response", "rejected", "missing-result", "missing-boards", "missing-own", "null-board", "null-entry", "missing-entries", "null-own", "exception" })
                { await Failure(mode); count++; }
                await Admission(); await Cancellation(); await Reentry(); count += 3;
            }
        }
        finally { CultureInfo.CurrentCulture = original; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: client achievement queries ({count} scenarios)");
    }

    private static async Task Protocol()
    {
        ClientSessionWork.Reset();
        var original = new ClashOfRimClientNetworkContext("http://wrong", "wrong", "wrong", "wrong", null, null, "wrong");
        var context = new ClashOfRimClientNetworkContext("http://localhost/base", "用户-é", "colony", "snapshot", null, null, "auth");
        int calls = 0; var bodies = new List<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++; bodies.Add(await request.Content!.ReadAsStringAsync(token));
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/base/" + ProtocolContractManifest.Find(ProtocolMessageKind.ListAchievements).Route.TrimStart('/'), "Achievement route/context changed.");
            using var body = JsonDocument.Parse(bodies.Last());
            Check(body.RootElement.GetProperty("userId").GetString() == context.UserId && body.RootElement.GetProperty("colonyId").GetString() == context.ColonyId
                && body.RootElement.GetProperty("currentSnapshotId").GetString() == "snapshot" && body.RootElement.EnumerateObject().Count() == 5, "Achievement request did not preserve the five-field public contract.");
            return new(HttpStatusCode.OK) { Content = new StringContent(ClientProtocolJson.Serialize(Reply()), Encoding.UTF8, "application/json") };
        }));
        var protocol = new ClientAchievementProtocol(new ClientProtocolTransport(http, original, new ClientProtocolEnvironment(static (_, _) => { })), () => "unconfigured");
        var target = ClientAchievementTarget.Resolve(context, "  Bob  ", "  colony-b  ");
        var response = await protocol.ListAsync(context, target, default);
        Check(response.Success && response.Response!.Leaderboards.Single().Entries.Single().Value == long.MaxValue - 1
            && response.Response.OwnAchievements.Single().Color == "Purple", "Achievement protocol lost long values or nested metadata.");
        var facade = new ClashOfRimModNetworkClient(http, context);
        Check((await facade.ListAchievementsAsync("  Bob  ", " colony-b ")).Success && calls == 2 && bodies[0] == bodies[1], "Facade duplicated or changed achievement request construction.");
        Check((await facade.ListAchievementsAsync()).Success, "Default target request failed.");
        using var own = JsonDocument.Parse(bodies.Last());
        Check(own.RootElement.GetProperty("targetUserId").GetString() == context.UserId && own.RootElement.GetProperty("targetColonyId").GetString() == context.ColonyId, "Default target borrowed another context.");
        Check(!(await protocol.ListAsync(new("", "", "", null, null, null, null), target, default)).Success && calls == 3, "Unconfigured achievement request reached HTTP.");
    }

    private static async Task Cache(bool hasGame)
    {
        var f = new Fixture(); f.Ports.HasGame = hasGame; int version = f.Module.Version;
        await f.Module.RefreshAsync("  Bob ", " colony-b ");
        var call = await f.Next();
        Check(call.Target == new ClientAchievementTarget("Bob", "colony-b") && call.Context.UserId == "alice" && call.Context.CurrentSnapshotId == "base", "Query lost captured identity/target.");
        var boards = f.Module.CopyLeaderboards(); var own = f.Module.CopyAchievements();
        Check(f.Module.Version == version + 1 && f.Module.Target == call.Target && !f.Module.Busy && f.Module.Message == "ClashOfRim.Achievement.StatusRefreshed"
            && boards[0].Entries[0].Value == long.MaxValue - 1 && own[0].Value == 42 && own[0].SourceSnapshotId == "source", "Achievement publication lost target/data/version/status coherence.");
        boards[0].Entries[0].UserId = "caller mutation"; own[0].Value = -1; boards.Clear(); own.Clear();
        f.Reply.Leaderboards[0].Entries[0].UserId = "response mutation"; f.Reply.OwnAchievements[0].Value = -2;
        Check(f.Module.CopyLeaderboards()[0].Entries[0].UserId == "ranked" && f.Module.CopyAchievements()[0].Value == 42, "Achievement cache exposed mutable nested DTOs.");
        version = f.Module.Version; f.Ports.Snapshot = "advanced";
        Check(f.Module.CopyLeaderboards().Count == 1 && f.Module.Version == version, "Ordinary snapshot advance erased achievements.");
        Check(f.Ports.Captures == 0 && f.Ports.Published == 0 && f.Ports.Queued == 0, "Read-only achievements captured or mutated game state.");
        ClientSessionWork.Reset();
        Check(f.Module.CopyLeaderboards().Count == 0 && f.Module.CopyAchievements().Count == 0 && f.Module.Target == new ClientAchievementTarget("", "")
            && f.Module.Message == "" && f.Module.Version > version, "Achievement state survived the ended session generation.");
    }

    private static async Task Replacement(bool oldFirst, string outcome)
    {
        var f = new Fixture();
        var older = new TaskCompletionSource<ModListAchievementsResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = new TaskCompletionSource<ModListAchievementsResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Send = (call, _) => call == 1 ? older.Task : newer.Task;
        var old = f.Module.RefreshAsync("old", "old-colony"); await f.Next();
        var current = f.Module.RefreshAsync("new", "new-colony"); await f.Next();
        void FinishOld()
        {
            if (outcome == "exception") older.SetException(new HttpRequestException("old failure"));
            else { var reply = Reply("old"); if (outcome == "rejected") reply.Result!.Accepted = false; older.SetResult(reply); }
        }
        if (oldFirst)
        {
            FinishOld(); await old.WaitAsync(TimeSpan.FromSeconds(3));
            Check(f.Module.Busy && f.Module.CopyAchievements().Count == 0, "Old completion cleared or published over a pending replacement.");
        }
        newer.SetResult(Reply("new")); await current.WaitAsync(TimeSpan.FromSeconds(3)); int version = f.Module.Version;
        if (!oldFirst) { FinishOld(); await old.WaitAsync(TimeSpan.FromSeconds(3)); }
        Check(!f.Module.Busy && f.Module.Target == new ClientAchievementTarget("new", "new-colony") && f.Module.CopyAchievements()[0].AchievementId == "new"
            && f.Module.Version == version && f.Diagnostics == 0 && f.Module.Message == "ClashOfRim.Achievement.StatusRefreshed", "Late response/failure replaced the latest requested target.");
    }

    private static async Task Ownership(string change, string phase)
    {
        var f = new Fixture();
        if (phase == "cache") { await f.Module.RefreshAsync(); f.Change(change); }
        else
        {
            if (phase == "send") f.OnSend = () => f.Change(change);
            else f.OnText = key => { if (key.EndsWith("StatusRefreshed", StringComparison.Ordinal)) f.Change(change); };
            await f.Module.RefreshAsync();
        }
        Check(f.Module.CopyLeaderboards().Count == 0 && f.Module.CopyAchievements().Count == 0 && f.Module.Target == new ClientAchievementTarget("", "")
            && !f.Module.Busy && f.Module.Message == "" && f.Diagnostics == 0, "Achievement crossed ownership: " + phase + "/" + change);
    }

    private static async Task Failure(string mode)
    {
        var f = new Fixture(); await f.Module.RefreshAsync("initial", "initial-colony"); int version = f.Module.Version;
        switch (mode)
        {
            case "network": f.NetworkFailure = true; break;
            case "null-response": f.Reply = null!; break;
            case "rejected": f.Reply.Result!.Accepted = false; f.Reply.Result.ErrorCode = (int)ProtocolErrorCode.Unauthorized; break;
            case "missing-result": f.Reply.Result = null; break;
            case "missing-boards": f.Reply.Leaderboards = null!; break;
            case "missing-own": f.Reply.OwnAchievements = null!; break;
            case "null-board": f.Reply.Leaderboards.Add(null!); break;
            case "null-entry": f.Reply.Leaderboards[0].Entries.Add(null!); break;
            case "missing-entries": f.Reply.Leaderboards[0].Entries = null!; break;
            case "null-own": f.Reply.OwnAchievements.Add(null!); break;
            case "exception": f.Send = (_, _) => throw new HttpRequestException("offline"); break;
        }
        await f.Module.RefreshAsync("failed", "failed-colony");
        Check(f.Module.Target == new ClientAchievementTarget("initial", "initial-colony") && f.Module.CopyAchievements()[0].Value == 42
            && f.Module.Version == version && !f.Module.Busy && f.Module.Message.StartsWith("ClashOfRim.Achievement.Status", StringComparison.Ordinal)
            && f.Module.Message != "ClashOfRim.Achievement.StatusRefreshed" && f.Diagnostics == (mode == "exception" ? 1 : 0), "Failed/incomplete reply replaced a valid cache: " + mode);
    }

    private static async Task Admission()
    {
        var f = new Fixture(); f.Ports.Server = "";
        await f.Module.RefreshAsync();
        Check(f.Calls == 0 && !f.Module.Busy && f.Module.Message == "ClashOfRim.Achievement.StatusNotConfigured", "Unconfigured query was admitted.");
        f.Ports.Server = "http://localhost"; f.Ports.Snapshot = ""; f.Ports.HasGame = false;
        await f.Module.RefreshAsync(" ", " ");
        Check(f.Module.Target == new ClientAchievementTarget("alice", "colony") && f.Module.CopyAchievements().Count == 1, "Public achievement query acquired a new game/snapshot prerequisite.");
    }

    private static async Task Cancellation()
    {
        var f = new Fixture();
        f.Send = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Reply(); };
        var task = f.Module.RefreshAsync(); var request = await f.Next();
        ClientSessionWork.Reset();
        await task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(request.Token.IsCancellationRequested && !f.Module.Busy && f.Module.Message == "" && f.Diagnostics == 0, "Session cancellation leaked a request or reported a stale failure.");
    }

    private static async Task Reentry()
    {
        var f = new Fixture(); f.OnText = key => { if (key.EndsWith("StatusRefreshing", StringComparison.Ordinal)) f.Module.Reset(); };
        await f.Module.RefreshAsync();
        Check(f.Calls == 0 && !f.Module.Busy && f.Module.Message == "", "Reset during admission retained the old request/status.");
        f.OnText = null; f.OnSend = () => f.Ports.Snapshot = "advanced";
        await f.Module.RefreshAsync();
        Check(f.Module.CopyAchievements().Count == 1 && (await f.Next()).Context.CurrentSnapshotId == "base", "Snapshot advancement changed an in-flight query context.");
        f.Reply = new() { Result = new() { Accepted = true }, Leaderboards = new(), OwnAchievements = new() };
        await f.Module.RefreshAsync();
        Check(f.Module.CopyLeaderboards().Count == 0 && f.Module.CopyAchievements().Count == 0, "Accepted empty achievements failed to replace history.");
    }

    private static ModListAchievementsResponseDto Reply(string id = "badge") => new()
    {
        Result = new() { Accepted = true },
        Leaderboards = new() { new() { AchievementId = id, Category = "Event", LabelKey = "标签", DescriptionKey = "描述", IconId = "icon", Color = "Purple",
            Entries = new() { new() { UserId = "ranked", ColonyId = "rank-colony", DisplayName = "文化 İ العربية", Value = long.MaxValue - 1, SourceSnapshotId = "rank-source" } } } },
        OwnAchievements = new() { new() { AchievementId = id, Category = "Event", LabelKey = "标签", DescriptionKey = "描述", Color = "Purple", IconId = "icon", Value = 42, SourceSnapshotId = "source" } }
    };
    private sealed record Call(ClashOfRimClientNetworkContext Context, ClientAchievementTarget Target, CancellationToken Token);
    private sealed class Fixture : IClientAchievementTransport, IClientAchievementPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientAchievementQueries Module;
        internal ModListAchievementsResponseDto Reply = ClientAchievementQueryTests.Reply();
        internal Func<int, CancellationToken, Task<ModListAchievementsResponseDto>>? Send;
        internal Action? OnSend;
        internal Action<string>? OnText;
        internal bool NetworkFailure;
        internal int Calls, Diagnostics;
        private readonly Channel<Call> calls = Channel.CreateUnbounded<Call>();
        internal Fixture()
        {
            ClientSessionWork.Reset();
            Session = new(Ports, Ports, new ClientSessionConnection(Ports)); Module = new(Session, Ports, this, this);
        }
        internal async Task<Call> Next() => await calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public async Task<ClashOfRimClientNetworkResult<ModListAchievementsResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, ClientAchievementTarget target, CancellationToken token)
        {
            int call = ++Calls; OnSend?.Invoke(); calls.Writer.TryWrite(new(context, target, token));
            if (NetworkFailure) return ClashOfRimClientNetworkResult<ModListAchievementsResponseDto>.Failed("Offline", "offline");
            return ClashOfRimClientNetworkResult<ModListAchievementsResponseDto>.Ok(Send is null ? Reply : await Send(call, token));
        }
        public string Text(string key, params (string Name, object? Value)[] arguments)
        { OnText?.Invoke(key); return key + (arguments.Length == 0 ? "" : ":" + string.Join(',', arguments.Select(argument => argument.Value))); }
        public void Diagnose(Exception exception) => Diagnostics++;
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "server": Ports.Server = "http://other"; break;
                case "game": Ports.GameOwner = new(); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break;
                case "reset": Module.Reset(); break;
            }
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
