using AIRsLight.ClashOfRim.Modules.Compatibility;
using System;
using AIRsLight.ClashOfRim;
using HarmonyLib;
using Verse;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientWorldEntryTests
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
                foreach (string mode in new[] { "network", "hello", "null", "rejected", "missing-result", "waiting", "missing-config", "missing-id", "missing-snapshot" })
                { await Failure(mode); count++; }
                foreach (string mode in new[] { "initial", "existing", "download" }) { await Success(mode); count++; }
                foreach (bool cancel in new[] { false, true }) { await Language(cancel); count++; }
                foreach (string change in new[] { "user", "colony", "server", "auth", "login", "generation", "game", "snapshot" })
                {
                    foreach (string phase in new[] { "confirmation", "registration", "publish", "accepted", "progress", "download", "queue" })
                    { await Ownership(change, phase); count++; }
                }
                foreach (string mode in new[] { "network", "null", "malformed", "exception", "discard" }) { await DownloadFailure(mode); count++; }
                await Replacement(); await PublicationFailure(); await PresentationReentry(); WarningPolicy(); StoreAdapter(); count += 5;
                foreach (string edit in new[] { "none", "local", "reset", "newer-source", "new-world-local" })
                    foreach (bool language in new[] { false, true }) { await AppearanceReceipt(edit, language); count++; }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: client world entry ({count} scenarios)");
    }
    private static async Task Failure(string mode)
    {
        var f = new Fixture(); var response = Reply(); string? failure = null;
        var result = ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Ok(response);
        switch (mode)
        {
            case "network": result = ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Failed("offline", "failed"); break;
            case "hello": failure = "hello-failed"; break;
            case "null": result = ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Ok(null!); break;
            case "rejected": response.Result!.Accepted = false; break;
            case "missing-result": response.Result = null; break;
            case "waiting": response.WorldConfigured = false; response.IsAdministrator = false; break;
            case "missing-config": response.WorldConfiguration = null; break;
            case "missing-id": response.WorldConfiguration!.WorldConfigurationId = " "; break;
            case "missing-snapshot": response.HasExistingColony = true; response.LatestSnapshotId = null; break;
        }
        await f.Run(result, failure);
        Check(f.Failures == 1 && f.Writes == 0 && f.Accepted == 0 && f.Downloads == 0 && f.Loads == 0 && f.Scenarios == 0
            && f.Ports.Colony == "colony" && f.Ports.Snapshot == "base" && !f.State.Pending && !f.Sync.IsBusy, "Invalid entry published identity/effects: " + mode);
    }
    private static async Task Success(string mode)
    {
        var f = new Fixture(); var response = Reply();
        if (mode == "initial") { response.WorldConfigured = false; response.RequiresInitialWorldConfiguration = true; response.LatestSnapshotId = null; }
        if (mode == "existing") response.HasExistingColony = true;
        var task = f.Run(Ok(response)); await f.Ports.PumpUntil(task);
        Check(f.Writes == 1 && f.Accepted == 1 && f.Failures == 0 && !f.Sync.IsBusy && f.Ports.Colony == "assigned"
            && f.Ports.Snapshot == (mode == "initial" ? "" : "latest"), "Accepted publication lost assignment/snapshot.");
        Check(f.Trace.IndexOf("store") < f.Trace.IndexOf("accepted"), "Domain projection preceded identity publication.");
        if (mode == "existing")
            Check(f.Loads == 1 && f.Downloads == 0 && f.Scenarios == 0 && !f.State.Pending && f.Reload!.ExpectedSnapshotId == "latest"
                && f.Reload.WorldConfiguration!.DifficultyValuesXml == "<difficulty>0.35</difficulty>", "Existing colony did not reuse snapshot reload.");
        else
        {
            Check(f.Scenarios == 1 && f.Loads == 0 && f.State.Pending, "New colony lost pending setup.");
            if (mode == "initial") Check(f.State.TakeInitial() is not null && f.Downloads == 0, "Initial administrator branch changed.");
            else
            {
                var generated = f.State.TakeGeneration()!;
                Check(f.DownloadContext!.ColonyId == "assigned" && f.DownloadContext.CurrentSnapshotId == "latest" && f.DownloadId == "world"
                    && generated.Substrate.PersistentRandomValue == 42 && generated.Configuration.Extensions[0].Metadata["culture"] == "tr-TR", "Download lost captured context or world metadata.");
            }
        }
    }
    private static async Task Language(bool cancel)
    {
        var f = new Fixture(); var response = Reply(language: true); response.HasExistingColony = true;
        await f.Run(Ok(response));
        Check(f.Confirm is not null && f.Sync.IsBusy && f.Writes == 0 && f.Accepted == 0 && !f.State.LanguageAccepted && !f.State.Pending,
            "Language prompt published entry before consent.");
        response.AssignedColonyId = "caller"; response.WorldConfiguration!.DifficultyValuesXml = "caller";
        f.Warning!.Result!.Accepted = false;
        if (cancel) f.Cancel!(); else f.Confirm!();
        f.Confirm!();
        Check(!f.Sync.IsBusy && f.Writes == (cancel ? 0 : 1) && f.Loads == (cancel ? 0 : 1) && f.State.LanguageAccepted == !cancel,
            "Language choice replayed or lost its continuation.");
        if (!cancel) Check(f.Ports.Colony == "assigned" && f.Reload!.WorldConfiguration!.DifficultyValuesXml == "<difficulty>0.35</difficulty>", "Prompt exposed mutable prepared response.");
    }
    private static async Task AppearanceReceipt(string edit, bool language)
    {
        var f = new Fixture(); f.Ports.Colony = "";
        var response = Reply(language); response.HasExistingColony = true;
        response.WorldConfiguration!.PlayerColonySites.Add(new() { UserId = "alice", ColonyId = "assigned", Tile = 4, TileLayerId = 7, Appearance = new() { ColorHex = "server" } });
        if (edit == "new-world-local") f.Publication.LocalWorldId = "before-world";
        f.On = phase =>
        {
            if (phase != "publish") return;
            if (edit is "local" or "reset") f.Publication.Appearance.SaveCurrent(ClientWorldPublicationTests.Selection(edit == "reset" ? "" : "local"));
            if (edit == "newer-source")
            {
                var config = ClientWorldPublicationTests.Data("newer"); config.PlayerColonySites[0].ColonyId = "assigned";
                f.Publication.Flow.Publish(f.Publication.Flow.Capture(), config, ClientWorldProjection.Sites, () => true);
            }
        };
        if (edit == "new-world-local") f.Publication.Hook = phase =>
        { if (phase == "remember") f.Publication.Appearance.SaveCurrent(ClientWorldPublicationTests.Selection("local")); };
        await f.Run(Ok(response));
        if (language) f.Confirm!();
        string expected = edit == "none" ? "server" : edit == "reset" ? "" : edit == "newer-source" ? "newer" : "local";
        Check(f.Ports.Colony == "assigned" && f.Loads == 1 && f.Publication.Appearance.Read().ColorHex == expected,
            "Colony/world adoption discarded the original appearance revision: " + edit);
    }
    private static async Task Ownership(string change, string phase)
    {
        var f = new Fixture(); var response = Reply(language: phase == "confirmation");
        bool kept = change == "snapshot";
        if (phase is "confirmation" or "registration")
        {
            response.HasExistingColony = true;
            if (phase == "registration") { response.Result!.Accepted = false; response.Result.ErrorCode = (int)ProtocolErrorCode.AccountNotFound; }
            await f.Run(Ok(response)); f.Change(change); f.Confirm!();
            if (phase == "registration") Check(f.Registered == (kept ? 1 : 0) && f.Writes == 0, "Registration window crossed identity: " + change);
            else Check(f.Loads == (kept ? 1 : 0) && f.Writes == (kept ? 1 : 0), "Language confirmation crossed identity: " + change);
        }
        else if (phase is "publish" or "accepted" or "progress")
        {
            f.On = at => { if (at == phase) f.Change(change); };
            var task = f.Run(Ok(response)); await f.Ports.PumpUntil(task);
            Check(f.Scenarios == (kept ? 1 : 0) && f.Downloads == (kept ? 1 : 0) && f.Accepted == (phase == "publish" && !kept ? 0 : 1),
                "Entry adopted a reentrant owner: " + phase + "/" + change);
            if (!kept) Check(!f.State.Pending, "Aborted entry retained setup after reentry.");
        }
        else
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Send = async _ => { await gate.Task; return Payload(); };
            var task = f.Run(Ok(response)); await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Action? queued = null;
            if (phase == "queue") { gate.SetResult(); queued = await f.Ports.Next(); }
            f.Change(change);
            if (phase == "download") gate.SetResult();
            if (queued is not null) queued();
            await f.Ports.PumpUntil(task);
            Check(f.Scenarios == (kept ? 1 : 0) && !f.Sync.IsBusy && f.State.Pending == kept, "Download/queue crossed owner or stranded setup: " + phase + "/" + change);
        }
        Check(!f.Sync.IsBusy, "Stale continuation retained manual admission: " + phase + "/" + change);
    }
    private static async Task DownloadFailure(string mode)
    {
        var f = new Fixture();
        f.Send = _ => mode switch
        {
            "network" => Task.FromResult(ClashOfRimClientNetworkResult<byte[]>.Failed("offline", "连接失败")),
            "null" => Task.FromResult(ClashOfRimClientNetworkResult<byte[]>.Ok(null!)),
            "malformed" => Task.FromResult(ClashOfRimClientNetworkResult<byte[]>.Ok(new byte[] { 1, 2, 3 })),
            "exception" => throw new InvalidOperationException("failed"),
            _ => Task.FromResult(Payload())
        };
        f.Discard = mode == "discard";
        await f.Ports.PumpUntil(f.Run(Ok(Reply())));
        Check(f.Failures == (mode == "discard" ? 0 : 1) && !f.State.Pending && f.Scenarios == 0 && !f.Sync.IsBusy, "Failed download stranded configuration/admission: " + mode);
    }
    private static async Task Replacement()
    {
        var f = new Fixture(); await f.Run(Ok(Reply(language: true))); var old = f.Confirm!; f.Cancel!();
        var next = Reply(); next.WorldConfigured = false; next.RequiresInitialWorldConfiguration = true;
        await f.Run(Ok(next)); old();
        Check(f.Writes == 1 && f.Scenarios == 1 && f.Loads == 0 && f.State.TakeInitial() is not null && !f.Sync.IsBusy, "Old cancelled window altered replacement.");
    }
    private static async Task PublicationFailure()
    {
        var f = new Fixture(); f.ThrowStore = true;
        await f.Run(Ok(Reply()));
        Check(f.Failures == 1 && f.Accepted == 0 && f.Ports.Colony == "colony" && f.Downloads == 0 && !f.State.Pending && !f.Sync.IsBusy, "Failed identity persistence continued entry.");
    }
    private static async Task PresentationReentry()
    {
        var f = new Fixture(); f.On = phase => { if (phase == "text") f.Change("generation"); };
        await f.Run(Ok(Reply()));
        Check(f.Writes == 0 && f.Accepted == 0 && f.Failures == 0 && f.Downloads == 0 && !f.Sync.IsBusy, "Formatting reentry published stale identity.");
    }
    private static void WarningPolicy()
    {
        var response = ClientCompatibilityWarning.FromPreparation(Reply(language: true));
        Check(ClientCompatibilityWarning.ShouldShow(response, false) && !ClientCompatibilityWarning.ShouldShow(response, true), "Language warning acknowledgment changed.");
        response.CompatibilityIssues[0].Code = "mod";
        Check(ClientCompatibilityWarning.ShouldShow(response, true), "Language acknowledgment suppressed unrelated mismatch.");
        response.CompatibilityIssues[0].Severity = "Info";
        Check(!ClientCompatibilityWarning.ShouldShow(response, false), "Accepted informational mismatch opened a warning.");
        response.Result!.Accepted = false;
        Check(ClientCompatibilityWarning.ShouldShow(response, false), "Rejected mismatch was hidden.");
        response.CompatibilityIssues.Clear(); response.Result.ErrorCode = (int)ProtocolErrorCode.ValidationFailed;
        response.ServerCompatibilityManifestJson = "{}";
        Check(ClientCompatibilityWarning.ShouldShow(response, false) && !ClientCompatibilityWarning.ShouldShow(null, false), "Legacy rejected manifest behavior changed.");
    }
    private static bool failWrite;
    private static int settingsWrites;
    private static bool SettingsWritePrefix()
    {
        settingsWrites++;
        if (failWrite) throw new InvalidOperationException("disk full");
        return false;
    }
    private static void StoreAdapter()
    {
        var harmony = new Harmony("ClashOfRim.Tests.WorldEntryStore");
        harmony.Patch(AccessTools.Method(typeof(ModSettings), nameof(ModSettings.Write)),
            prefix: new HarmonyMethod(typeof(ClientWorldEntryTests), nameof(SettingsWritePrefix)));
        try
        {
            failWrite = false; settingsWrites = 0;
            var settings = new ClashOfRimSettings { ColonyId = "old", CurrentSnapshotId = "old-snapshot" };
            var store = new ClientWorldEntryStore(settings);
            store.Publish("old", "old-snapshot");
            Check(settingsWrites == 0, "Unchanged identity was persisted again.");
            failWrite = true;
            try { store.Publish("new", "new-snapshot"); } catch (InvalidOperationException) { }
            Check(settings.ColonyId == "old" && settings.CurrentSnapshotId == "old-snapshot", "Failed settings write left partial identity in memory.");
            failWrite = false; store.Publish("new", "new-snapshot");
            Check(settingsWrites == 2 && settings.ColonyId == "new" && settings.CurrentSnapshotId == "new-snapshot", "Identity fields were not saved together.");
            settings.ServerBaseUrl = "old-server"; settings.UserId = "old-user"; settings.OfflinePassword = "old-password";
            settings.CurrentLineageToken = "lineage";
            failWrite = true;
            try { store.Initialize("new-server", "new-user", "new-password"); } catch (InvalidOperationException) { }
            Check(settings.ServerBaseUrl == "old-server" && settings.UserId == "old-user" && settings.OfflinePassword == "old-password"
                && settings.ColonyId == "new" && settings.CurrentSnapshotId == "new-snapshot" && settings.CurrentLineageToken == "lineage", "Failed entry input save left partial settings.");
            failWrite = false; store.Initialize("new-server", "new-user", "  password  ");
            Check(settingsWrites == 4 && settings.ServerBaseUrl == "new-server" && settings.UserId == "new-user" && settings.OfflinePassword == "  password  "
                && settings.ColonyId == "" && settings.CurrentSnapshotId == "" && settings.CurrentLineageToken == "", "Entry input did not clear colony lineage together.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
    private static ModPrepareWorldSessionResponseDto Reply(bool language = false) => new()
    {
        Result = new() { Accepted = true }, AssignedColonyId = "assigned", LatestSnapshotId = "latest", IsAdministrator = true, WorldConfigured = true,
        CompatibilityIssues = language ? new() { new() { Code = "GameLanguageMismatch", Severity = "Warning" } } : new(),
        WorldConfiguration = new() { WorldConfigurationId = "world", GameLanguage = "ChineseSimplified", DifficultyValuesXml = "<difficulty>0.35</difficulty>",
            Extensions = new() { new() { ProviderId = "culture", Metadata = new() { ["culture"] = "tr-TR" } } } }
    };
    private static ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> Ok(ModPrepareWorldSessionResponseDto response) => ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Ok(response);
    private static ClashOfRimClientNetworkResult<byte[]> Payload() => ClashOfRimClientNetworkResult<byte[]>.Ok(WorldSubstratePackageCodec.Encode(new(42, "<grid/>", "<features/>", "<landmarks/>", new byte[] { 1, 2, 3 })));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IClientWorldEntryStore, IClientWorldSubstrateTransport, IClientWorldEntryPresentation, IClientSnapshotReload
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldSetupState State;
        internal readonly ClientWorldPublicationTestPorts Publication;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientWorldEntry Module;
        internal readonly List<string> Trace = new();
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Writes, Accepted, Failures, Downloads, Loads, Scenarios, Registered;
        internal bool ThrowStore, Discard;
        internal Action? Confirm, Cancel;
        internal Action<string>? On;
        internal ModLoginResponseDto? Warning;
        internal ClientSnapshotReloadRequest? Reload;
        internal ClashOfRimClientNetworkContext? DownloadContext;
        internal string? DownloadId;
        internal Func<CancellationToken, Task<ClashOfRimClientNetworkResult<byte[]>>>? Send;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports)); State = new(Session);
            Publication = new(Session, Ports);
            Module = new(Session, Ports, State, this, this, this, this, Publication.Flow);
            Module.Accepted += _ => { Accepted++; Trace.Add("accepted"); On?.Invoke("accepted"); };
        }
        internal async Task Run(ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto> result, string? failure = null)
        {
            Check(Sync.TryBeginManual(out var lease), "Fixture admission busy.");
            try { await Module.Handle(result, failure, lease!, Module.Capture()); } finally { lease!.Dispose(); }
        }
        public void Initialize(string server, string user, string password) => throw new NotSupportedException();
        public void Publish(string colonyId, string snapshotId)
        {
            if (ThrowStore) throw new InvalidOperationException("disk full");
            Writes++; Ports.Colony = colonyId; Ports.Snapshot = snapshotId; Trace.Add("store"); On?.Invoke("publish");
        }
        public async Task<ClashOfRimClientNetworkResult<byte[]>> DownloadAsync(ClashOfRimClientNetworkContext context, string id, CancellationToken token)
        {
            Downloads++; DownloadContext = context; DownloadId = id; Sent.TrySetResult();
            return Send is null ? Payload() : await Send(token);
        }
        public Task StartAsync(ClientSnapshotReloadRequest request, ClientSynchronizationCoordinator.Lease? continuation = null)
        {
            Check(continuation?.IsCurrent == true, "Reload lost exact admission.");
            using var owned = continuation!.TransferManual(); Reload = request; Loads++; return Task.CompletedTask;
        }
        public string Text(string key, params (string Name, object? Value)[] arguments) { On?.Invoke("text"); return key; }
        public void Reject(string message) => throw new NotSupportedException();
        public void Failure(string message) { Failures++; Trace.Add("failure"); }
        public void Progress(string message, float progress) { On?.Invoke("progress"); }
        public void Scenario(string? message) { Scenarios++; Trace.Add("scenario"); }
        public void CloseProgress() { }
        public void ConfirmRegistration(string account, Action accept) => Confirm = accept;
        public void Register(string server, string account) { Registered++; Check(server == "http://localhost" && account == "alice", "Registration reread mutable settings."); }
        public void Compatibility(ModLoginResponseDto response, string? language, bool languageAccepted, Func<bool> current, Action? accept = null, Action? cancel = null)
        { if (!ClientCompatibilityWarning.ShouldShow(response, languageAccepted)) { cancel?.Invoke(); return; } Warning = response; Confirm = accept; Cancel = cancel; }
        public void Enqueue(Action action, Action discard) => Ports.Enqueue(() => { if (Discard) discard(); else action(); });
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break;
                case "server": Ports.Server += "/other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "game": Ports.GameOwner = new(); break;
                case "snapshot": Ports.Snapshot = "advanced"; break;
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new" }); break;
            }
        }
    }
}
