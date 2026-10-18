using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientColonyAbandonmentTests
{
    private static int scenarios;
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; scenarios = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Success(); await Admission(); await DuplicateAndBusy(); await Errors(); await Queue(); await Replacement();
                foreach (string change in Changes)
                {
                    await ConfirmationOwnership(change);
                    foreach (string stage in new[] { "submitting", "network", "queued", "success-text" }) await RequestOwnership(stage, change);
                }
                foreach (string stage in new[] { "ended", "identity", "success-status", "success-message" }) await ExitReentry(stage);
                foreach (string outcome in new[] { "accepted", "http", "reject", "exception", "login", "snapshot" }) await EarlyDisconnect(outcome);
                await FrozenFailure(); await CleanupFailure(); await ExitPresentationFailure();
            }
            Console.WriteLine($"  colony abandonment: {scenarios} scenarios checked");
        }
        catch (Exception ex) { throw new InvalidOperationException($"Abandonment scenario {scenarios}: {ex}"); }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static readonly string[] Changes = { "user", "auth", "server", "colony", "login", "generation", "world-id", "game", "world", "snapshot", "target" };
    private static async Task Success()
    {
        var f = new Fixture(); f.Open(); Check(f.ConfirmationLabel == "基地 Î", "Confirmation label lost.");
        f.Ports.Snapshot = "latest"; f.Label = "changed after open";
        await f.Run(f.ConfirmAction!());
        Check(f.Context!.UserId == "alice" && f.Context.CurrentSnapshotId == "latest" && f.Context.AuthToken == "auth", "Request adopted wrong identity or confirmation snapshot.");
        Check(f.Key!.StartsWith("abandon-colony:alice:colony:latest:", StringComparison.Ordinal), "Request key lost captured colony identity.");
        Check(f.Ends == 1 && f.Menus == 1 && f.Logouts == 0 && f.Session.SessionId is null && f.Ports.Auth == "" && f.Ports.Snapshot == "" && f.Lineage == "",
            "Accepted abandonment did not use one complete local exit without another server logout.");
        Check(f.Trace.IndexOf("cleanup") < f.Trace.IndexOf("persist") && f.Trace.IndexOf("persist") < f.Trace.IndexOf("success-message"), "Exit persistence/message order changed.");
        Check(f.Messages.Count == 1 && f.Messages[0].Text.Contains("StatusSucceeded") && f.Messages[0].Kind == ClientWorldWriteMessage.Progress && !f.Module.Busy, "Success presentation changed.");
        await f.ConfirmAction!(); Check(f.Calls == 1, "Consumed confirmation was replayed."); scenarios++;
    }
    private static async Task Admission()
    {
        foreach (string missing in new[] { "user", "server", "colony", "auth", "unavailable", "target" })
        {
            var f = new Fixture();
            switch (missing)
            {
                case "user": f.Ports.User = ""; break; case "server": f.Ports.Server = ""; break;
                case "colony": f.Ports.Colony = ""; break; case "auth": f.Ports.Auth = ""; break;
                case "unavailable": f.Session.BeginUnavailable(f.Ports.Auth, null); break; case "target": f.TargetAvailable = false; break;
            }
            f.Open(); Check(f.ConfirmAction is null && f.Calls == 0, "Invalid abandonment confirmation offered: " + missing);
            Check(f.Messages.Count == (missing == "target" ? 0 : 1), "Admission message changed."); scenarios++;
        }
        await Task.CompletedTask;
    }
    private static async Task ConfirmationOwnership(string change)
    {
        var f = new Fixture(); f.Open(); var confirm = f.ConfirmAction!; f.Change(change); await f.Run(confirm());
        Check(f.Calls == (change == "snapshot" ? 1 : 0), "Old window submitted a replacement identity/world: " + change); scenarios++;
    }
    private static async Task RequestOwnership(string stage, string change)
    {
        var f = new Fixture(); f.Open(); f.Once(stage, () => f.Change(change)); await f.Run(f.ConfirmAction!());
        Check(f.Ends == 0 && f.Menus == 0 && f.Messages.All(message => !message.Text.Contains("StatusSucceeded")), "Old receipt ended a different owner: " + stage + "/" + change);
        Check(!f.Module.Busy, "Stale request retained busy state."); scenarios++;
    }
    private static async Task DuplicateAndBusy()
    {
        var f = new Fixture { Hold = NewSignal() }; f.Open(); var firstConfirm = f.ConfirmAction!; f.Open(); var secondConfirm = f.ConfirmAction!;
        Task first = firstConfirm(); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstConfirm(); await secondConfirm(); int windows = f.Windows; f.Open();
        Check(f.Calls == 1 && f.Windows == windows && f.Module.Busy && f.Messages.Count == 2, "Duplicate click/window bypassed busy admission.");
        f.Hold.SetResult(); await f.Run(first); Check(f.Ends == 1 && f.Menus == 1, "Busy attempt changed active completion."); scenarios++;
    }
    private static async Task Errors()
    {
        foreach (string fault in new[] { "http", "null", "reject", "missing-result", "exception", "cancel" })
        {
            var f = new Fixture { Fault = fault }; f.Open(); await f.Run(f.ConfirmAction!());
            Check(f.Ends == 0 && f.Menus == 0 && f.Ports.Auth == "auth" && !f.Module.Busy && f.Messages.Count == 1, "Failure cleared state or lost notice: " + fault);
            string? key = f.Key; f.Fault = null; f.Open(); await f.Run(f.ConfirmAction!());
            Check(f.Calls == 2 && f.Menus == 1 && f.Key != key, "Failed attempt poisoned retry or reused submission identity."); scenarios++;
        }
    }
    private static async Task Queue()
    {
        foreach (bool discard in new[] { false, true })
        {
            var f = new Fixture { DuplicateQueue = true, DiscardQueue = discard }; f.Open(); Task work = f.ConfirmAction!();
            Action queued = await f.Ports.Next(); Check(f.Module.Busy && f.Ends == 0, "Busy ended before publication.");
            queued(); await work;
            Check(f.Ends == (discard ? 0 : 1) && f.Menus == (discard ? 0 : 1) && !f.Module.Busy, "Duplicate/discarded callback replayed exit."); scenarios++;
        }
    }
    private static async Task Replacement()
    {
        var f = new Fixture { Hold = NewSignal() }; f.Open(); Task old = f.ConfirmAction!(); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Change("game"); f.Open(); Task replacement = f.ConfirmAction!(); Action queued = await f.Ports.Next();
        f.Hold.SetResult(); await old;
        Check(f.Module.Busy, "Old finally cleared replacement request."); queued(); await replacement;
        Check(f.Ends == 1 && f.Calls == 2 && f.Menus == 1, "Old response crossed the game boundary."); scenarios++;
    }
    private static async Task ExitReentry(string stage)
    {
        var f = new Fixture(); f.Open(); f.Once(stage, () => f.Change("login")); await f.Run(f.ConfirmAction!());
        Check(f.Session.SessionId == "replacement" && f.Ports.Auth == "new-auth" && f.Menus == 0, "Exit cleared/left a replacement session: " + stage);
        if (stage == "ended") Check(f.Cleanups == 0, "Old exit continued domain cleanup after new login."); scenarios++;
    }
    private static async Task EarlyDisconnect(string outcome)
    {
        var f = new Fixture(); f.Open(); f.Fault = outcome is "http" or "reject" or "exception" ? outcome : null;
        f.Once("network", () =>
        {
            var invalidation = f.Session.BeginUnavailable(f.Ports.Auth, f.Session.SessionId)!;
            f.Session.WhenUnavailableReady(invalidation, () => f.Ports.Enqueue(() => { if (f.Session.CompleteUnavailable(invalidation)) f.ExpiryPrompts++; }));
            Check(f.ExpiryPrompts == 0 && f.Ports.Auth == "auth", "Server close invalidated terminal request before HTTP receipt.");
            if (outcome == "login") f.Change("login");
            if (outcome == "snapshot") f.Change("snapshot");
        });
        await f.Run(f.ConfirmAction!());
        Check(f.Menus == (outcome == "accepted" ? 1 : 0), "Terminal close/receipt order changed exit: " + outcome);
        Check(f.ExpiryPrompts == (outcome is "http" or "reject" or "exception" or "snapshot" ? 1 : 0), "Deferred expiry was lost or applied after accepted exit: " + outcome);
        if (outcome == "login") Check(f.Ports.Auth == "new-auth", "Old deferred expiry erased new login."); scenarios++;
    }
    private static async Task FrozenFailure()
    {
        var f = new Fixture { Fault = "reject" }; f.Open(); f.OnQueue = () => f.Response.Result!.Message = "changed";
        await f.Run(f.ConfirmAction!()); Check(f.Messages.Single().Text.Contains("original") && !f.Messages.Single().Text.Contains("changed"), "Failure message borrowed queued response."); scenarios++;
    }
    private static async Task CleanupFailure()
    {
        var f = new Fixture(); f.Open(); f.Once("persist", () => throw new InvalidOperationException("persist failed"));
        await f.Run(f.ConfirmAction!());
        Check(f.Menus == 0 && f.Messages.Count == 0 && f.GameErrors.Count == 1 && !f.Module.Busy, "Cleanup failure falsely reported success or retained request."); scenarios++;
    }
    private static async Task ExitPresentationFailure()
    {
        foreach (string stage in new[] { "success-status", "success-message", "menu" })
        {
            var f = new Fixture(); f.Open(); f.Once(stage, () => throw new InvalidOperationException("exit presentation failed"));
            await f.Run(f.ConfirmAction!());
            Check(f.Ends == 1 && f.Ports.Auth == "" && f.Diagnostics.Count == 1 && f.GameErrors.Count == 0,
                "Committed exit lost its failure diagnostic in the cancelled worker: " + stage); scenarios++;
        }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IClientWorldIdentity, IClientWorldCatalogRuntime, IClientColonyAbandonTransport, IClientColonyAbandonPresentation,
        IClientSessionIdentityStore, IClientSessionLogoutTransport
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientColonyAbandonment Module;
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly TaskCompletionSource Entered = NewSignal();
        internal readonly List<string> Trace = new();
        internal readonly List<(string Text, ClientWorldWriteMessage Kind)> Messages = new();
        internal readonly List<Exception> GameErrors = new();
        internal readonly List<string> Diagnostics = new();
        internal TaskCompletionSource? Hold;
        internal Func<Task>? ConfirmAction;
        internal string? ConfirmationLabel, Key, Fault;
        internal string Label = "基地 Î", Lineage = "lineage";
        internal int Calls, Ends, Cleanups, Menus, Logouts, Windows, ExpiryPrompts;
        internal bool TargetAvailable = true, DuplicateQueue, DiscardQueue;
        internal Action<string>? Hook;
        internal Action? OnQueue;
        internal object World = new();
        public string WorldId { get; private set; } = "world-a";
        internal ClashOfRimClientNetworkContext? Context;
        internal readonly ModAbandonPlayerColonyResponseDto Response = new() { Result = new() { Accepted = true, Message = "original" }, WorldConfiguration = new() { WorldConfigurationId = "unused" } };
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(this, this, new(Ports)); Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "session" });
            Session.Ended += () => { Ends++; Stage("ended"); };
            Session.Ended += () => { Cleanups++; Ports.Snapshot = ""; Lineage = ""; WorldId = ""; Mutations.Reset(); Trace.Add("cleanup"); };
            Session.IdentityChanged += response => { if (response is null) Stage("identity"); };
            Module = new(Session, this, this, this, this); Trace.Clear();
        }
        internal void Open() => Module.Open(new Target(this, Ports.GameOwner, World));
        internal Task Run(Task work) => Ports.PumpUntil(work, error => GameErrors.Add(error));
        public ClashOfRimClientNetworkContext ReadContext() => Ports.ReadContext();
        public void Write(string? user, string? name, string auth) { if (auth.Length == 0) Stage("persist"); Ports.Write(user, name, auth); }
        public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string sessionId)
        { Logouts++; return Task.FromResult(ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Ok(new() { Result = new() { Accepted = true } })); }
        public IClientWorldCatalogSession Bind() => new Bound(this, Ports.GameOwner, World);
        public void Confirm(string colony, Func<Task> confirm) { ConfirmationLabel = colony; ConfirmAction = confirm; Windows++; }
        public string Text(string key, params (string Name, object? Value)[] args)
        { if (key.EndsWith("StatusSucceeded")) Stage("success-text"); return key + " " + string.Join(" ", args.Select(value => value.Name + "=" + value.Value)); }
        public void Status(string text) { Stage(text.Contains("StatusSucceeded") ? "success-status" : text.Contains("StatusSubmitting") ? "submitting" : "failure-status"); }
        public void Message(string text, ClientWorldWriteMessage kind)
        { Messages.Add((text, kind)); if (text.Contains("StatusSucceeded")) Stage("success-message"); }
        public void Diagnose(string message) => Diagnostics.Add(message);
        public async Task<ClashOfRimClientNetworkResult<ModAbandonPlayerColonyResponseDto>> AbandonAsync(ClashOfRimClientNetworkContext context, string key, CancellationToken token)
        {
            Calls++; Context = context; Key = key; Entered.TrySetResult(); if (Calls == 1 && Hold is not null) await Hold.Task;
            Stage("network");
            if (Fault == "http") return ClashOfRimClientNetworkResult<ModAbandonPlayerColonyResponseDto>.Failed("network", "failed");
            if (Fault == "null") return ClashOfRimClientNetworkResult<ModAbandonPlayerColonyResponseDto>.Ok(null!);
            if (Fault == "exception") throw new InvalidOperationException("network exception");
            if (Fault == "cancel") throw new OperationCanceledException();
            Response.Result = Fault == "missing-result" ? null : new() { Accepted = Fault != "reject", Message = "original" };
            return ClashOfRimClientNetworkResult<ModAbandonPlayerColonyResponseDto>.Ok(Response);
        }
        internal void Stage(string stage) { Trace.Add(stage); Hook?.Invoke(stage); }
        internal void Once(string stage, Action action) => Hook = value => { if (value == stage) { Hook = null; action(); } };
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "auth": Ports.Auth = "new-auth"; break;
                case "server": Ports.Server = "http://other"; break; case "colony": Ports.Colony = "new-colony"; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = "new-auth", SessionId = "replacement" }); break;
                case "generation": ClientSessionWork.Reset(); break; case "world-id": WorldId = "new-world"; break;
                case "game": Ports.GameOwner = new(); break; case "world": World = new(); break;
                case "snapshot": Ports.Snapshot = "advanced"; break; case "target": TargetAvailable = false; break;
            }
        }
        private sealed record Bound(Fixture F, object Game, object World) : IClientWorldCatalogSession
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public void Enqueue(Action action, Action discard)
            { F.OnQueue?.Invoke(); F.Ports.Enqueue(() => { F.Stage("queued"); if (F.DiscardQueue) discard(); else action(); if (F.DuplicateQueue) action(); }); }
        }
        private sealed record Target(Fixture F, object Game, object World) : IClientColonyAbandonTarget
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public bool IsAvailable => F.TargetAvailable && IsCurrent;
            public string Label => F.Label;
            public void ReturnToMenu() { F.Stage("menu"); F.Menus++; _ = F.Session.End(sendLogout: true); }
        }
    }
}
