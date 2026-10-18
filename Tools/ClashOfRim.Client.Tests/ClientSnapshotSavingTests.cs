using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientSnapshotSavingTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture; int cases = 0;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var kind in Enum.GetValues<ClientSnapshotSaveKind>())
                {
                    using (var f = new Fixture())
                    {
                        await f.Run(kind); await f.Registrations(kind == ClientSnapshotSaveKind.Manual ? 1 : 2);
                        Require(f.Uploads == 1 && f.Ports.Published == 1 && f.Ports.GamePublished == 1 && f.Ports.Snapshot == "saved" && !f.Sync.IsBusy, "Save did not publish/release once.");
                        Require(f.UploadKind == (kind == ClientSnapshotSaveKind.Autosave ? ModSnapshotUploadKinds.AutoSave : ModSnapshotUploadKinds.ManualUpload)
                            && f.Ports.CaptureModes.SequenceEqual(new[] { false }), "Save changed upload kind or removed raid state.");
                        Require(f.Messages.SequenceEqual(new[] { (kind + ":Succeeded", true) }) && f.Exits == 0 && f.Session.SessionId == "session", "Normal save changed message/exit policy.");
                        Require(f.ExtensionRequests == (kind == ClientSnapshotSaveKind.Manual ? 1 : 0)
                            && f.SiteCaptures == (kind == ClientSnapshotSaveKind.Manual ? 0 : 1), "Forced extension refresh and ordinary site registration were conflated.");
                        if (kind != ClientSnapshotSaveKind.Manual) Require(f.Appearance?.ColorHex == "#123456", "Saved-game site registration lost appearance.");
                        Require(f.SentCatalog.All(call => call.UserId == "alice" && call.AuthToken == "auth" && call.CurrentSnapshotId == "saved"), "Catalog used a different save owner.");
                        cases++;
                    }
                    foreach (string denial in new[] { "mutation", "busy", "no-session", "no-snapshot", "unconfigured" })
                    {
                        using var f = new Fixture(); ClientSynchronizationCoordinator.Lease? foreign = null;
                        if (denial == "mutation") f.Mutations.Begin(LocalMutationKind.Bank, "pending");
                        else if (denial == "busy") f.Sync.TryBeginManual(out foreign);
                        else if (denial == "no-session") f.Session.ClearIdentity();
                        else if (denial == "no-snapshot") f.Ports.Snapshot = "";
                        else f.Ports.User = "";
                        bool tracked = denial is "mutation" or "busy";
                        Require(f.Module.CanUseMenu == tracked && f.Module.InterceptsAutosave == (tracked || denial == "no-snapshot"), "Menu/autosave eligibility drifted.");
                        await f.Run(kind);
                        bool denied = denial is "mutation" or "busy" || kind != ClientSnapshotSaveKind.Manual;
                        if (denied)
                        {
                            Require(f.Ports.Captures == 0 && f.Uploads == 0 && f.Exits == 0 && f.SentCatalog.IsEmpty, "Denied save performed work.");
                            Require(f.Messages.Count == (kind == ClientSnapshotSaveKind.Autosave ? 0 : 1), "Autosave skip became an interactive failure or manual denial was silent.");
                        }
                        if (foreign is not null) { Require(foreign.IsCurrent, "Save denial released another operation."); foreign.Dispose(); }
                        cases++;
                    }
                    foreach (string failure in new[] { "capture", "network", "rejected", "exception", "publication" })
                    {
                        using var f = new Fixture { Mode = failure };
                        if (failure == "capture") f.Ports.CaptureFailure = new("controlled", "capture", SnapshotUploadMessage.SaveFailed);
                        if (failure == "publication") f.Ports.OnPublish = () => throw new IOException("publication");
                        await f.Run(kind, exit: true, gameFailure: ex => Require(ex is IOException, "Unexpected engine failure."));
                        Require(!f.Sync.IsBusy && f.Messages.Count == 1 && !f.Messages.Single().Success && f.Exits == 0 && f.SentCatalog.IsEmpty, "Failed save continued or leaked admission: " + failure);
                        Require(f.Notices.Last().Stage == (failure is "exception" or "publication" ? ClientSnapshotSaveStage.Exception : ClientSnapshotSaveStage.Failed), "Failure semantics changed: " + failure);
                        cases++;
                    }
                    foreach (string stage in new[] { "admission-started", "format-Uploading", "status-Uploading", "capture", "upload", "publish", "admission-ended", "queued", "format-Succeeded", "status-Succeeded", "message-Succeeded", "appearance" })
                    foreach (string change in new[] { "user", "server", "colony", "auth", "snapshot", "game", "world", "world-object", "login", "same-login", "generation", "mutation" })
                    {
                        if (stage == "appearance" && kind == ClientSnapshotSaveKind.Manual) continue;
                        using var f = new Fixture(); bool reached = false;
                        f.Hook = point => { if (point == stage) { reached = true; f.Hook = null; f.Change(change); } };
                        await f.Run(kind, exit: true);
                        Require(reached, "Did not reach " + stage);
                        Require(f.Exits == 0 && f.SentCatalog.IsEmpty && f.SiteCaptures == 0 && f.ExtensionRequests == 0,
                            "Stale save continued: " + kind + "/" + stage + "/" + change);
                        Require(!f.Sync.IsBusy, "Stale save leaked admission: " + stage + "/" + change);
                        if (change == "mutation") Require(f.Mutations.IsPending, "Save cleared another mutation.");
                        cases++;
                    }
                }
                await ExitCases(); cases += 9;
                await Replacement(); cases++;
                await PresentationExceptions(); cases += 4;
            }
            Console.WriteLine($"PASS: snapshot saving workflow ({cases} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private static async Task ExitCases()
    {
        foreach (string change in new[] { "none", "cleanup-settings", "cleanup-login", "completed-login", "completed-game", "completed-world", "nested", "exit-throws", "logout-fails" })
        {
            using var f = new Fixture(); int cleanups = 0;
            f.Session.Ended += () =>
            {
                cleanups++;
                if (change == "cleanup-login") f.Change("same-login");
                if (change == "cleanup-settings") { f.Sync.Reset(); f.Ports.Snapshot = ""; f.World.LocalWorldId = ""; }
            };
            f.Session.ExitCompleted += _ =>
            {
                if (change == "completed-login") f.Change("same-login");
                if (change == "completed-game") f.Change("game");
                if (change == "completed-world") f.Change("world-object");
            };
            f.ExitAction = () =>
            {
                Require(!f.Sync.IsBusy && string.IsNullOrEmpty(f.Ports.Auth), "Exit preceded save release/auth clearing.");
                if (change == "nested") _ = f.Session.End(true);
                if (change == "exit-throws") throw new IOException("scene");
            };
            f.Mode = change;
            await f.Run(ClientSnapshotSaveKind.Menu, exit: true);
            await f.Logout.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool exit = change != "cleanup-login" && !change.StartsWith("completed", StringComparison.Ordinal);
            Require(f.Exits == (exit ? 1 : 0) && cleanups == 1 && f.Logouts.Count == 1, "Exit receipt/order/cleanup changed: " + change);
            var released = f.Logouts.Single();
            Require(released.Context.AuthToken == "auth" && released.Context.CurrentSnapshotId == "saved" && released.Id == "session", "Logout borrowed replacement credentials.");
            if (change.Contains("login", StringComparison.Ordinal)) Require(f.Ports.Auth == "auth" && f.Session.SessionId == "session", "Old save cleared a new login.");
        }
    }
    private static async Task Replacement()
    {
        using var f = new Fixture(); Task? next = null;
        f.Hook = stage =>
        {
            if (stage != "admission-ended") return;
            f.Hook = null; f.Ports.SaveBytes = SnapshotPackageModuleTests.Save("saved");
            next = f.Module.StartAsync(ClientSnapshotSaveKind.Manual);
        };
        await f.Run(ClientSnapshotSaveKind.Menu, exit: true);
        Require(next is not null, "Did not start a replacement save.");
        await f.Ports.PumpUntil(next!); await f.Registrations(1);
        Require(f.Exits == 0 && f.Uploads == 2 && f.Messages.Count == 1 && f.Messages.Single().Text == "Manual:Succeeded" && !f.Sync.IsBusy,
            "Old completion overwrote/released the save started during admission release.");
    }
    private static async Task PresentationExceptions()
    {
        foreach (string stage in new[] { "admission-started", "format-Uploading", "status-Uploading", "format-Succeeded" })
        {
            using var f = new Fixture();
            f.Hook = point => { if (point == stage) { f.Hook = null; throw new IOException("view"); } };
            try { await f.Run(ClientSnapshotSaveKind.Menu, exit: true, gameFailure: ex => Require(ex is IOException, "Unexpected presentation error.")); }
            catch (IOException ex) when (stage == "admission-started" && ex.Message == "view") { }
            Require(!f.Sync.IsBusy && f.Exits == 0 && f.SentCatalog.IsEmpty, "Presentation exception leaked admission/continued save: " + stage);
        }
    }
    private sealed class Fixture : IClientSnapshotTransport, IClientSnapshotSavingPresentation, IClientSessionLogoutTransport,
        IClientColonyRegistrationRuntime, IClientColonyRegistrationTransport, IClientColonyAppearanceStore, IDisposable
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldPublicationTestPorts World;
        internal readonly ClientSnapshotSaving Module;
        internal readonly List<ClientSnapshotSaveNotice> Notices = new();
        internal readonly List<(string Text, bool Success)> Messages = new();
        internal readonly ConcurrentQueue<ClashOfRimClientNetworkContext> SentCatalog = new();
        internal readonly ConcurrentQueue<(ClashOfRimClientNetworkContext Context, string Id)> Logouts = new();
        internal readonly TaskCompletionSource Logout = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim registered = new(0);
        internal object WorldOwner = new();
        internal Action<string>? Hook;
        internal Action? ExitAction;
        internal string Mode = "";
        internal string? UploadKind;
        internal int Uploads, Exits, ExtensionRequests, SiteCaptures;
        internal ModColonyAppearanceDto? Appearance;
        private ClientSnapshotSaveNotice? last;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, this, new ClientSessionConnection(Ports));
            Session.PublishLogin(new() { AuthToken = "auth", SessionId = "session" });
            World = new(Session, Ports, world: () => WorldOwner);
            var registration = new ClientColonyRegistration(Session, World, Mutations, this, this, World.Flow);
            registration.Diagnostic += (message, _) => { if (message.Contains("refresh requested", StringComparison.Ordinal)) ExtensionRequests++; };
            Module = new(Session, Sync, Mutations, new(Ports, Ports, this, Sync), Ports, World, World, registration,
                new ClientColonyAppearance(Session, this, World), this);
            Ports.OnCapture = () => Hook?.Invoke("capture"); Ports.OnPublish = () => Hook?.Invoke("publish");
            int queued = 0; Ports.BeforeGameAction = () => { if (++queued == 3) Hook?.Invoke("queued"); };
            Sync.SnapshotChanged += notice => Hook?.Invoke(notice.Phase == SnapshotTransactionPhase.Started ? "admission-started" : "admission-ended");
        }
        internal Task Run(ClientSnapshotSaveKind kind, bool exit = false, Action<Exception>? gameFailure = null)
            => Ports.PumpUntil(Module.StartAsync(kind, exit ? () => { Exits++; ExitAction?.Invoke(); } : null), gameFailure);
        internal async Task Registrations(int count)
        { for (int i = 0; i < count; i++) await Ports.PumpUntil(registered.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(t => Require(t.Result, "Catalog followup did not start."))); }
        public string Format(ClientSnapshotSaveNotice notice) { Notices.Add(notice); last = notice; Hook?.Invoke("format-" + notice.Stage); return notice.Kind + ":" + notice.Stage; }
        public void Status(string message) => Hook?.Invoke("status-" + last!.Stage);
        public void Message(string message, bool success) { Messages.Add((message, success)); Hook?.Invoke("message-" + last!.Stage); }
        public void Diagnose(ClientSnapshotSaveNotice notice, string message) { }
        public Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string key,
            ModSnapshotPackageMetadataDto package, byte[] bytes, string? confirmation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements, CancellationToken token)
        {
            Uploads++; UploadKind = package.SnapshotUploadKind;
            Require(confirmation is null && key.Contains(context.UserId) && bytes.Length > 0, "Changed upload contract."); Hook?.Invoke("upload");
            if (Mode == "exception") throw new IOException("transport");
            return Task.FromResult(Mode == "network" ? ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Failed("HttpError", "network")
                : ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Ok(new() { Result = new() { Accepted = Mode != "rejected" }, AcceptedSnapshotId = "saved", NextLineageToken = "lineage" }));
        }
        public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string id)
        {
            Logouts.Enqueue((context, id)); Logout.TrySetResult();
            if (Mode == "logout-fails") throw new IOException("logout");
            return Task.FromResult(ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Ok(new() { Result = new() { Accepted = true } }));
        }
        public IClientColonyRegistrationSession Bind() => new Bound(this, Ports.GameOwner, WorldOwner);
        public Task<ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>> RegisterAsync(ClashOfRimClientNetworkContext context,
            IReadOnlyCollection<ModPlayerColonySiteDto> sites, IReadOnlyCollection<ModWorldConfigurationExtensionDto>? extensions, bool suppress, CancellationToken token)
        { SentCatalog.Enqueue(context); registered.Release(); return Task.FromResult(ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>.Failed("controlled", "stop after domain request")); }
        public ClientColonyAppearanceAccount ReadAccount() => World.ReadAccount();
        public string? Read(string key) { Hook?.Invoke("appearance"); return ClientColonyAppearanceCodec.Encode(new("Manual", "icon", "", "#123456")); }
        public void Write(string key, string? value) => throw new NotSupportedException();
        public void Persist() => throw new NotSupportedException();
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "snapshot": Ports.Snapshot = "other"; break; case "game": Ports.GameOwner = new(); break;
                case "world": World.LocalWorldId = "other"; break; case "world-object": WorldOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthToken = "next", SessionId = "next" }); break;
                case "same-login": Session.PublishLogin(new() { AuthToken = "auth", SessionId = "session" }); break;
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); break;
                case "mutation": Mutations.Begin(LocalMutationKind.Bank, "next"); break;
            }
        }
        public void Dispose() { Sync.Reset(); ClientSessionWork.Reset(); }
        private sealed record Bound(Fixture F, object Game, object World) : IClientColonyRegistrationSession
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.WorldOwner);
            public bool Playing => true;
            public bool HasWorld => true;
            public bool HasWorldObjects => true;
            public bool HasColonyContext => true;
            public bool HasExtensionCollectors => true;
            public bool HasRemoteSession => false;
            public IReadOnlyCollection<ModPlayerColonySiteDto> CaptureSites(string user, string colony, ModColonyAppearanceDto? appearance)
            { F.SiteCaptures++; F.Appearance = appearance; return new[] { new ModPlayerColonySiteDto { UserId = user, ColonyId = colony, Tile = 4 } }; }
            public IReadOnlyCollection<ModWorldConfigurationExtensionDto> CaptureExtensions(string user, string colony, string snapshot)
                => new[] { new ModWorldConfigurationExtensionDto { ProviderId = "test", Kind = "test", PayloadJson = "{}" } };
            public void Enqueue(Action action, Action discard) => F.Ports.Enqueue(() => { if (IsCurrent) action(); else discard(); });
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
