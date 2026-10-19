using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientWorldEntryStartTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        int count = 0; var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string server in new[] { "", " ", "ftp://localhost", "http://", "https://", "ws://", "wss://", "//", "http://[broken" }) { Invalid(server, "alice"); count++; }
                foreach (string? user in new[] { null, "", " \t\r\n " }) { Invalid("localhost", user); count++; }
                foreach (var (server, expected) in new[] { (" localhost:5000/ ", "http://localhost:5000"), ("//localhost/base/", "http://localhost/base"),
                    ("https://localhost/base/", "https://localhost/base"), ("wss://localhost/base", "https://localhost/base"), ("ws://localhost/base", "http://localhost/base") })
                    foreach (bool create in new[] { false, true }) { await Input(server, expected, create); count++; }
                foreach (bool snapshot in new[] { false, true }) { Busy(snapshot); count++; }
                foreach (string change in new[] { "user", "server", "colony", "auth", "login", "game", "generation", "snapshot" })
                    foreach (string phase in new[] { "store", "format", "progress", "scheduled", "hello", "queue" }) { await Ownership(change, phase); count++; }
                foreach (string phase in new[] { "identity", "store", "progress", "format" }) { await Reentry(phase); count++; }
                foreach (string phase in new[] { "identity", "store", "progress", "hello", "discard" }) { await Failure(phase); count++; }
                Barrier(); count++;
                foreach (bool changedWorld in new[] { false, true }) { await DelayedWorldObservation(changedWorld); count++; }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: client world entry start ({count} scenarios)");
    }
    private static void Invalid(string server, string? user)
    {
        var f = new Fixture(); var token = ClientSessionWork.Token;
        Check(!f.Start.TryStart(server, user, "secret") && f.Rejections == 1 && f.Ends == 0 && f.Writes == 0 && !f.Sync.IsBusy
            && token == ClientSessionWork.Token && f.Ports.Auth == "auth", $"Invalid input changed session before validation: [{server}]/[{user}], rejected={f.Rejections}, ended={f.Ends}, writes={f.Writes}, failures={f.Failures}.");
    }
    private static async Task DelayedWorldObservation(bool changedWorld)
    {
        var f = new Fixture(); var delay = new ClientWorldPublicationTestPorts.DelayedReply();
        f.BeforePrepare = delay.Wait;
        Check(f.Start.TryStart("localhost", "alice", "password"), "Delayed entry did not begin.");
        await delay.Complete(f.Ports, f.Idle(), () => { if (changedWorld) f.Publication.LocalWorldId = "replaced-during-http"; });
        Check(f.Prepares == 1 && f.Scenarios == (changedWorld ? 0 : 1) && f.Ports.Colony == (changedWorld ? "" : "assigned") && !f.Sync.IsBusy,
            "World entry captured its observation after HTTP or lost its unchanged continuation.");
    }
    private static async Task Input(string server, string expected, bool create)
    {
        var f = new Fixture(); var old = ClientSessionWork.Token;
        Check(f.Start.TryStart(server, "  用户-I  ", "  密码  ", create), "Valid entry rejected.");
        Check(f.Ends == 1 && f.Writes == 1 && f.Sync.IsBusy && f.Barrier.BlocksCurrent && old.IsCancellationRequested, "Entry did not use one exit boundary and retain admission.");
        Check(f.Ports.User == "用户-I" && f.Ports.Server == expected && f.Password == "  密码  "
            && f.Ports.Colony == "" && f.Ports.Snapshot == "" && f.Ports.Auth == "", "Input normalization or identity reset changed.");
        var queued = await f.Ports.Next();
        Check(f.Scenarios == 0 && f.Sync.IsBusy, "Preparation published before main-thread handoff."); queued(); await f.Idle();
        Check(f.Hellos == 1 && f.Prepares == 1 && f.Create == create && f.Scenarios == 1 && f.Failures == 0 && f.State.TakeInitial() is not null,
            "Prepared entry lost account-creation intent or initial setup.");
        Check(f.Captured!.ServerBaseUrl == f.Ports.Server && f.Captured.UserId == "用户-I" && f.Captured.ColonyId == "", "Preparation borrowed another context.");
    }
    private static void Busy(bool snapshot)
    {
        var f = new Fixture();
        ClientSynchronizationCoordinator.Lease? lease;
        Check(snapshot ? f.Sync.TryAcquireSnapshot(out lease) : f.Sync.TryBeginManual(out lease), "Fixture admission failed.");
        using (lease)
        {
            Check(!f.Start.TryStart("localhost", "new", "password") && f.Ends == 0 && f.Writes == 0 && f.Rejections == 1
                && f.Sync.IsBusy && f.Ports.Auth == "auth", "Busy entry cleared another operation or session.");
        }
        Check(!f.Sync.IsBusy, "Entry changed the busy lease owner.");
    }
    private static async Task Ownership(string change, string phase)
    {
        var f = new Fixture(); bool keep = change == "snapshot";
        bool accepted;
        if (phase == "scheduled")
        {
            // Hold the real publication gate so the background continuation cannot
            // capture state until after this deliberate identity/Game transition.
            using (ClientSessionWork.EnterPublication())
            { accepted = f.Start.TryStart("localhost", "alice", "password"); f.Change(change); }
        }
        else
        {
            f.On = at => { if (at == phase) f.Change(change); };
            accepted = f.Start.TryStart("localhost", "alice", "password");
        }
        if (phase == "queue")
        {
            var queued = await f.Ports.Next(); f.Change(change); queued();
        }
        else if (keep)
        {
            Check(accepted, "Ordinary snapshot advancement rejected entry.");
            (await f.Ports.Next())();
        }
        if (phase == "hello") await f.Changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Idle();
        Check(f.Scenarios == (keep ? 1 : 0) && f.Failures == 0 && !f.Sync.IsBusy, "Entry crossed captured owner: " + phase + "/" + change);
        if (!keep && phase is "store" or "format" or "progress" or "scheduled") Check(f.Hellos == 0, "Deferred preparation adopted a new identity.");
    }
    private static async Task Reentry(string phase)
    {
        var f = new Fixture(); int attempts = 0;
        f.On = at =>
        {
            if (at != phase) return;
            attempts++; Check(!f.Start.TryStart("other", "other", "other"), "Synchronous callback reentered identity transition.");
        };
        Check(f.Start.TryStart("localhost", "alice", "password"), "Outer entry rejected during callback.");
        (await f.Ports.Next())(); await f.Idle();
        Check(attempts > 0 && f.Ends == 1 && f.Writes == 1 && f.Hellos == 1 && f.Scenarios == 1, "Reentry repeated cleanup or network work.");
    }
    private static async Task Failure(string phase)
    {
        var f = new Fixture();
        f.ThrowAt = phase;
        bool accepted = f.Start.TryStart("localhost", "alice", "password");
        if (phase is "hello" or "discard") (await f.Ports.Next())();
        await f.Idle();
        Check(accepted == (phase is "hello" or "discard") && f.Scenarios == 0 && f.Failures == (phase == "discard" ? 0 : 1)
            && !f.Sync.IsBusy && !f.State.Pending, "Entry failure leaked continuation/state: " + phase);
        f.ThrowAt = null;
        Check(f.Start.TryStart("localhost", "retry", "password"), "Failed start retained synchronous guard.");
        (await f.Ports.Next())(); await f.Idle(); Check(f.Scenarios == 1, "Entry could not retry after failure.");
    }
    private static void Barrier()
    {
        var f = new Fixture(); object old = f.Ports.GameOwner;
        f.Barrier.BlockCurrent(); Check(f.Barrier.BlocksCurrent, "Current source game not blocked.");
        f.Session.PublishLogin(new() { AuthenticatedUserId = "other", AuthToken = "other", SessionId = "new" });
        Check(f.Barrier.BlocksCurrent, "Identity change released the still-running old scene.");
        Check(!f.Barrier.Initialized(old) && f.Barrier.BlocksCurrent, "Old Game initialization admitted setup or removed its own barrier.");
        var next = new object(); Check(!f.Barrier.Initialized(next) && f.Barrier.BlocksCurrent, "Unrelated Game callback admitted setup or removed the current barrier.");
        f.Ports.GameOwner = next; Check(!f.Barrier.BlocksCurrent, "New game was blocked by old identity.");
        f.Barrier.Initialized(old); f.Ports.GameOwner = old; Check(f.Barrier.BlocksCurrent, "Late old initialization cleared a different game's guard.");
        f.Ports.GameOwner = next; Check(f.Barrier.Initialized(next), "Current new Game could not submit setup."); f.Ports.GameOwner = old; Check(!f.Barrier.BlocksCurrent, "Completed new-game transition retained the source reference.");
        f.Barrier.BlockCurrent(); f.Barrier.Loaded(); Check(!f.Barrier.BlocksCurrent, "Authoritative load did not release source game.");
        f.Ports.HasGame = false; f.Barrier.BlockCurrent(); f.Ports.HasGame = true; Check(!f.Barrier.BlocksCurrent, "Menu entry captured a nonexistent game.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal sealed class Fixture : IClientWorldEntryStore, IClientWorldPreparationTransport, IClientWorldPreparationPresentation,
        IClientWorldEntryPresentation, IClientWorldSubstrateTransport, IClientSnapshotReload, IClientWorldSourceGame, IClientSessionIdentityStore, IClientServerMetadata
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientWorldSetupState State;
        internal readonly ClientWorldPublicationTestPorts Publication;
        internal readonly ClientWorldSourceGame Barrier;
        internal readonly ClientWorldEntryStart Start;
        internal readonly TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Ends, Writes, Rejections, Failures, Hellos, Prepares, Scenarios;
        internal string? Password, ThrowAt, Rejected;
        internal bool Create;
        internal ClashOfRimClientNetworkContext? Captured;
        internal Action<string>? On;
        internal Func<Task>? BeforePrepare;
        internal ModPrepareWorldSessionResponseDto? PrepareResponse;
        internal Action<string, Action>? ConfirmAccount;
        internal Action<string, string>? RegisterAccount;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(this, Ports, new ClientSessionConnection(Ports)); State = new(Session); Barrier = new(this);
            Session.Ended += () => { Ends++; Sync.Reset(); Barrier.BlockCurrent(); State.Reset(); Ports.Snapshot = ""; };
            var preparation = new ClientWorldPreparation(Session, Ports, this, this);
            Publication = new(Session, Ports);
            var entry = new ClientWorldEntry(Session, Ports, State, this, this, this, this, Publication.Flow, this);
            Start = new(Session, Sync, Ports, this, this, preparation, entry);
        }
        public object? Current => Ports.HasGame ? Ports.GameOwner : null;
        public Task Apply(string? manifest, bool administrator, bool refreshAdministrator, Func<bool> current)
        {
            Check(!refreshAdministrator, "Entry start unexpectedly refreshed administrator status.");
            return Task.CompletedTask;
        }
        public ClashOfRimClientNetworkContext ReadContext() => Ports.ReadContext();
        public void Write(string? user, string? displayName, string auth)
        { Visit("identity"); if (user is not null) Ports.User = user; Ports.Auth = auth; }
        public void Initialize(string server, string user, string password)
        {
            Check(Sync.IsBusy, "Input persistence ran outside manual admission.");
            if (ThrowAt == "store") throw new InvalidOperationException("settings failed");
            Writes++; Ports.Server = server; Ports.User = user; Ports.Colony = ""; Ports.Snapshot = ""; Password = password; Visit("store");
        }
        public void Publish(string colonyId, string snapshotId) { Ports.Colony = colonyId; Ports.Snapshot = snapshotId; }
        public Task<ClashOfRimClientNetworkResult<ModServerHelloResponseDto>> HelloAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        {
            Hellos++; Captured = context; Visit("hello");
            return Task.FromResult(ClashOfRimClientNetworkResult<ModServerHelloResponseDto>.Ok(new()
            { Result = new() { Accepted = true }, ProtocolVersion = ClashOfRimVersion.ProtocolVersion,
                ProtocolMajor = ClashOfRimVersion.ProtocolMajor, MinimumSupportedProtocolMajor = ClashOfRimVersion.ProtocolMajor,
                ProtocolMinor = ClashOfRimVersion.ProtocolMinor, MinimumSupportedProtocolMinor = ClashOfRimVersion.ProtocolMinor }));
        }
        public async Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, bool createAccount, CancellationToken token)
        {
            Prepares++; Create = createAccount;
            if (BeforePrepare is not null) await BeforePrepare();
            return ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Ok(PrepareResponse ?? new()
            { Result = new() { Accepted = true }, IsAdministrator = true, RequiresInitialWorldConfiguration = true, AssignedColonyId = "assigned" });
        }
        public Task<ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, bool generation, CancellationToken token) => throw new NotSupportedException();
        public Task<ClashOfRimClientNetworkResult<byte[]>> DownloadAsync(ClashOfRimClientNetworkContext context, string id, CancellationToken token) => throw new NotSupportedException();
        public Task StartAsync(ClientSnapshotReloadRequest request, ClientSynchronizationCoordinator.Lease? continuation = null) => throw new NotSupportedException();
        public string Text(string key, params (string Name, object? Value)[] arguments) { Visit("format"); return key; }
        public void Diagnose(string message) { }
        public void Reject(string message) { Rejections++; Rejected = message; Visit("reject"); }
        public void Failure(string message) => Failures++;
        public void Progress(string message, float progress) => Visit("progress");
        public void Scenario(string? message) => Scenarios++;
        public void CloseProgress() { }
        public void ConfirmRegistration(string account, Action accept)
        { if (ConfirmAccount is null) throw new NotSupportedException(); ConfirmAccount(account, accept); }
        public void Register(string server, string account)
        { if (RegisterAccount is null) throw new NotSupportedException(); RegisterAccount(server, account); }
        public void Compatibility(ModLoginResponseDto response, string? language, bool languageAccepted, Func<bool> current, Action? accept = null, Action? cancel = null) { }
        public void Enqueue(Action action, Action discard) => Ports.Enqueue(() => { if (ThrowAt == "discard") discard(); else action(); });
        private void Visit(string phase) { if (ThrowAt == phase) throw new InvalidOperationException(phase + " failed"); On?.Invoke(phase); }
        internal async Task Idle() { using var next = await Sync.AcquireManualAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new" }); break;
                case "game": Ports.GameOwner = new(); break;
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); break;
                case "snapshot": Ports.Snapshot = "advanced"; break;
            }
            Changed.TrySetResult();
        }
    }
}
