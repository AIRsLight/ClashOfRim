using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using HarmonyLib;
using Verse;

internal static class SnapshotUploaderModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        ClientSessionWork.Reset();
        try
        {
            SharedPreparationAndPublication();
            await SuccessAndAchievements();
            await FailureResults();
            await SharedAdmission();
            await DeferredPublication();
            await ReplacedOwners();
            await CancellationAndDispatchFailures();
            await PublicFacadeSharesAdmission();
            await PublicFacadeWithoutGame();
        }
        finally { ClientSessionWork.Reset(); ClientSnapshotComposition.Synchronization.Reset(); }
    }

    private static void SharedPreparationAndPublication()
    {
        var f = new Fixture();
        f.Game.Achievements.Add(new() { AchievementId = "pending", EventKey = "event" });
        var preparation = new ClientSnapshotPreparation(f.Game);
        Require(preparation.TryBuild("domain-owner", "domain-colony", out var build, out var reason)
            && reason is null && f.Game.Captures == 1 && !f.Game.RemovedRaidSessions
            && build.Package!.OwnerId == "domain-owner" && build.Package.ColonyId == "domain-colony"
            && build.Package.PreviousSnapshotId == "base" && build.Package.DefenderThreatPoints == 42f
            && build.Package.SnapshotUploadKind is null && build.SourcePath == "memory"
            && Encoding.UTF8.GetString(build.OriginalPayload!).Contains("clashOfRimPendingAchievementCandidates")
            && f.Trace.Count == 0 && f.Game.Achievements.Count == 1,
            "Domain preparation lost capture metadata, removed unsubmitted achievements or published state.");
        using (var zipped = new GZipStream(new MemoryStream(build.Payload!), CompressionMode.Decompress))
        using (var reader = new StreamReader(zipped))
            Require(reader.ReadToEnd() == Encoding.UTF8.GetString(build.OriginalPayload!), "Prepared payload differs from captured save.");

        f.Game.CaptureFailure = new("SaveToMemoryFailed", "save failure", SnapshotUploadMessage.SaveFailed);
        Require(!preparation.TryBuild("alice", "colony", out build, out reason)
            && build.ErrorCode == "SaveToMemoryFailed" && reason?.Message == "save failure", "Capture failure was replaced by a build failure.");
        f.Game.CaptureFailure = null; f.Game.ThrowOnCapture = true;
        Require(!preparation.TryBuild("alice", "colony", out build, out reason)
            && build.ErrorCode == "SaveToMemoryException" && reason?.Message?.Contains("fixture capture") == true, "Capture exception was lost.");
        f.Game.ThrowOnCapture = false; f.Game.CancelOnCapture = true;
        try { preparation.TryBuild("alice", "colony", out _, out _); throw new Exception("Canceled capture became a normal failure."); }
        catch (OperationCanceledException) { }
        f.Game.CancelOnCapture = false; f.Game.Malformed = true;
        Require(!preparation.TryBuild("alice", "colony", out build, out reason)
            && build.ErrorCode == "XmlException" && reason?.Code == "XmlException" && !string.IsNullOrWhiteSpace(reason.Message), "Invalid XML failure lost its code.");

        foreach (string? token in new string?[] { null, "", "  ", "accepted-token" })
        {
            f = new Fixture();
            f.Game.Achievements.Add(new() { AchievementId = "pending", EventKey = "event" });
            var publication = new ClientSnapshotPublication(f.Store, f.Game);
            publication.PublishCurrentGame(" ", "ignored");
            Require(f.Trace.Count == 0, "Empty accepted snapshot published state.");
            publication.PublishCurrentGame("domain-accepted", token);
            bool hasToken = !string.IsNullOrWhiteSpace(token);
            Require(f.Store.Snapshot == "domain-accepted" && f.Store.Token == (hasToken ? token : "old-lineage")
                && f.Game.Snapshot == (hasToken ? "domain-accepted" : "base") && f.Store.Writes == 1
                && f.Game.Published!.Count == 0 && f.Game.Achievements.Count == 1
                && f.Trace.SequenceEqual(new[] { "store", "game", "persist" }),
                "Domain publication lost lineage, consumed pending achievements or changed persistence ordering.");
        }
    }

    private static async Task SuccessAndAchievements()
    {
        var f = new Fixture();
        var queued = new ModSnapshotAchievementCandidateDto { AchievementId = " achievement ", EventKey = " event ", Value = 7 };
        f.Game.Achievements.Add(queued);
        var direct = new[]
        {
            new ModSnapshotAchievementCandidateDto { AchievementId = "achievement", EventKey = "event", Value = 99 },
            new ModSnapshotAchievementCandidateDto { AchievementId = "direct", EventKey = "unique", Value = 3,
                Category = "c", LabelKey = "l", IconId = "i", Color = "red", AggregationKind = "sum", MetadataJson = "{}" },
            new ModSnapshotAchievementCandidateDto { AchievementId = "", EventKey = "invalid" }
        };
        f.Game.Defer = true;
        Task<ClientSnapshotUploadResult> upload = f.Upload(achievements: direct);
        direct[1].Value = 1000;
        (await f.Game.Next()).Run();
        var publication = await f.Game.Next();
        Require(f.Sync.SnapshotInProgress && !upload.IsCompleted && f.Store.Snapshot == "base" && f.Store.Writes == 0,
            "Upload returned or released admission before game publication.");
        var request = f.Transport.Requests.Single();
        Require(request.Context.CurrentSnapshotId == "base" && request.Context.AuthToken == "auth"
            && request.Package.OwnerId == "alice" && request.Package.ColonyId == "colony"
            && request.Package.PreviousSnapshotId == "base" && request.Package.LineageToken == "old-lineage"
            && request.Package.DefenderThreatPoints == 42f && request.Package.SnapshotUploadKind == "fixture-kind"
            && request.Confirmation == "fixture-confirm" && f.Game.RemovedRaidSessions,
            "Capture or upload lost identity, lineage, classification or game options.");
        Require(request.Key == $"snapshot-upload:alice:colony:{request.Package.SnapshotId}"
            && request.Achievements.Count == 2 && request.Achievements[0].Value == 7
            && request.Achievements[1].Value == 3 && request.Achievements[1].MetadataJson == "{}"
            && request.Achievements[1].Category == "c" && request.Achievements[1].Color == "red",
            "Candidate merge order, deduplication or capture changed.");
        using (var zipped = new GZipStream(new MemoryStream(request.Payload), CompressionMode.Decompress))
        using (var reader = new StreamReader(zipped))
        {
            string xml = reader.ReadToEnd();
            Require(!xml.Contains("clashOfRimPendingAchievementCandidates") && xml.Contains("clashOfRimLineageSnapshotId"),
                "Upload retained the pending queue or stripped snapshot lineage.");
        }
        publication.Run();
        var result = await upload;
        Require(result.Failure is null && result.AcceptedSnapshotId == "accepted-1" && result.SourcePath == "memory"
            && f.Store.Snapshot == "accepted-1" && f.Game.Snapshot == "accepted-1" && f.Store.Token == "next"
            && f.Game.Published!.Count == 2 && f.Store.Writes == 1 && !f.Sync.IsBusy,
            "Successful upload did not publish the accepted snapshot exactly once.");
        Require(f.Trace.SequenceEqual(new[] { "store", "game", "persist" }), "Publication ordering changed.");
    }

    private static async Task FailureResults()
    {
        var cases = new (Action<Fixture> Arrange, string Code, SnapshotUploadMessage Reason)[]
        {
            (f => f.Store.User = "", "NotConfigured", SnapshotUploadMessage.NotConfigured),
            (f => { f.Store.Snapshot = ""; f.Game.Owner = null; }, "UntrackedSaveFile", SnapshotUploadMessage.NoRunningGame),
            (f => f.Game.Snapshot = "", "UntrackedSaveFile", SnapshotUploadMessage.LineageMissing),
            (f => f.Game.Snapshot = "different", "UntrackedSaveFile", SnapshotUploadMessage.LineageMismatch),
            (f => f.Game.CaptureFailure = new("SaveToMemoryFailed", "fixture", SnapshotUploadMessage.SaveFailed),
                "SaveToMemoryFailed", SnapshotUploadMessage.SaveFailed),
            (f => f.Game.ThrowOnCapture = true, "SaveToMemoryException", SnapshotUploadMessage.SaveFailed),
            (f => f.Game.Malformed = true, "XmlException", SnapshotUploadMessage.BuildFailed),
            (f => f.Transport.Failure = ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Failed("NetworkError", "fixture"),
                "NetworkError", SnapshotUploadMessage.UploadFailed),
            (f => f.Transport.Failure = ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Ok(new()
                { Result = new() { Accepted = false, ErrorCode = 9, Message = "rejected" } }), "9", SnapshotUploadMessage.ServerRejected)
        };
        foreach (var test in cases)
        {
            var f = new Fixture(); test.Arrange(f);
            var result = await f.Upload();
            Require(result.Failure?.Code == test.Code && result.Failure.Fallback == test.Reason
                && f.Trace.Count == 0 && f.Store.Writes == 0 && !f.Sync.IsBusy, "Failure leaked publication or admission: " + test.Code);
        }
        var initial = new Fixture(); initial.Store.Snapshot = "";
        Require((await initial.Upload()).Failure is null, "Initial snapshot without a prior lineage was rejected.");
        foreach (string? missing in new string?[] { null, "", "  " })
        {
            var fallback = new Fixture();
            fallback.Transport.Failure = ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Ok(new()
                { AcceptedSnapshotId = missing, NextLineageToken = missing });
            var accepted = await fallback.Upload();
            Require(accepted.AcceptedSnapshotId == fallback.Transport.Requests.Single().Package.SnapshotId
                && fallback.Store.Token == "old-lineage" && fallback.Game.Snapshot == "base" && fallback.Store.Writes == 1,
                "Response fallbacks for snapshot ID and absent lineage token changed.");
        }
    }

    private static async Task SharedAdmission()
    {
        var f = new Fixture();
        Require(f.Sync.TryBeginManual(out var manual), "Manual fixture admission failed.");
        var waiting = f.Upload();
        Require(!waiting.IsCompleted && f.Game.Captures == 0, "Uploader bypassed manual preparation.");
        manual!.Dispose();
        Require((await waiting).Failure is null && !f.Sync.IsBusy, "Waiting upload did not resume or release admission.");

        f = new Fixture();
        f.Transport.Hold = NewResponse();
        var first = f.Upload();
        await f.Transport.Started.Task;
        var second = f.Upload();
        Require(f.Game.Captures == 1 && !second.IsCompleted, "Parallel public uploads overlapped capture.");
        f.Transport.Hold.SetResult(Accepted("accepted-1"));
        await first; await second;
        Require(f.Game.Captures == 2 && f.Transport.Requests[1].Context.CurrentSnapshotId == "accepted-1" && !f.Sync.IsBusy,
            "Queued upload did not capture the published lineage of its predecessor.");

        f = new Fixture();
        Require(f.Sync.TryAcquireSnapshot(out var borrowed), "Snapshot fixture admission failed.");
        f.Transport.Hold = NewResponse();
        first = f.Upload(borrowed); await f.Transport.Started.Task;
        await Throws<InvalidOperationException>(() => f.Upload(borrowed));
        Require(f.Sync.SnapshotInProgress, "Duplicate execution released its caller's admission.");
        f.Transport.Hold.SetResult(Accepted("accepted")); await first;
        Require(f.Sync.SnapshotInProgress, "Uploader released borrowed domain admission.");
        borrowed!.Dispose();
        await Throws<OperationCanceledException>(() => f.Upload(borrowed));
        Require(!f.Sync.IsBusy, "Stale continuation allocated another transaction.");
    }

    private static async Task DeferredPublication()
    {
        foreach (string replacement in new[] { "game", "identity", "lineage", "admission", "session" })
        {
            var f = new Fixture(); f.Game.Defer = true;
            var task = f.Upload(); (await f.Game.Next()).Run();
            var pending = await f.Game.Next();
            switch (replacement)
            {
                case "game": f.Game.Owner = new(); break;
                case "identity": f.Store.Auth = "replacement"; break;
                case "lineage": f.Store.Snapshot = "other"; break;
                case "admission": f.Sync.Reset(); break;
                case "session": ClientSessionWork.Reset(); f.Sync.Reset(); break;
            }
            pending.Run(); await Throws<OperationCanceledException>(() => task);
            Require(f.Trace.Count == 0 && !f.Sync.IsBusy, "Replaced " + replacement + " accepted a late publication.");
        }
    }

    private static async Task ReplacedOwners()
    {
        foreach (bool accepted in new[] { false, true })
        {
            var pending = new Fixture(); pending.Transport.Hold = NewResponse();
            var request = pending.Upload(); await pending.Transport.Started.Task;
            pending.Store.Auth = "new-identity";
            pending.Transport.Hold.SetResult(accepted ? Accepted("old-response")
                : ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Failed("NetworkError", "old failure"));
            await Throws<OperationCanceledException>(() => request);
            Require(pending.Trace.Count == 0 && !pending.Sync.IsBusy, "Replaced identity received a stale success or failure.");
        }
        var f = new Fixture();
        Require(f.Sync.TryBeginManual(out var manual), "Manual fixture failed.");
        var task = f.Upload(); f.Store.Auth = "new-account"; manual!.Dispose();
        await Throws<OperationCanceledException>(() => task);
        Require(f.Game.Captures == 0 && !f.Sync.IsBusy, "Waiting operation captured a replacement identity.");

        f = new Fixture(); f.Transport.Hold = NewResponse();
        task = f.Upload(); await f.Transport.Started.Task;
        ClientSessionWork.Reset(); f.Sync.Reset();
        Require(f.Sync.TryAcquireSnapshot(out var next), "New session admission failed.");
        f.Store.User = "bob";
        f.Transport.Hold.SetResult(Accepted("old-response"));
        await Throws<OperationCanceledException>(() => task);
        Require(f.Store.User == "bob" && f.Store.Writes == 0 && f.Sync.SnapshotInProgress,
            "Late response changed new identity or released new admission.");
        next!.Dispose();
    }

    private static async Task CancellationAndDispatchFailures()
    {
        foreach (string stage in new[] { "before", "waiting", "capture", "publication" })
        {
            var f = new Fixture(); using var cancel = new CancellationTokenSource();
            ClientSynchronizationCoordinator.Lease? held = null;
            if (stage == "before") { cancel.Cancel(); f.Store.User = ""; }
            if (stage == "waiting") Require(f.Sync.TryBeginManual(out held), "Manual fixture failed.");
            f.Game.Defer = true;
            var task = f.Upload(cancellation: cancel.Token);
            GameWork? pending = null;
            if (stage is "capture" or "publication")
            {
                pending = await f.Game.Next();
                if (stage == "publication") { pending.Run(); pending = await f.Game.Next(); }
            }
            cancel.Cancel(); await Throws<OperationCanceledException>(() => task); pending?.Run();
            Require(f.Trace.Count == 0 && (held is null ? !f.Sync.IsBusy : f.Sync.ManualInProgress),
                "Cancellation changed game state or another owner's admission at " + stage);
            held?.Dispose();
        }
        var discard = new Fixture(); discard.Game.Defer = true;
        var discarded = discard.Upload(); (await discard.Game.Next()).Discard();
        await Throws<OperationCanceledException>(() => discarded);
        Require(!discard.Sync.IsBusy, "Discarded game action stranded upload admission.");
        var throwing = new Fixture(); throwing.Game.ThrowOnEnqueue = true;
        await Throws<InvalidOperationException>(() => throwing.Upload());
        Require(!throwing.Sync.IsBusy, "Enqueue failure leaked upload admission.");
        throwing = new Fixture(); throwing.Store.ThrowOnPersist = true;
        await Throws<InvalidOperationException>(() => throwing.Upload());
        Require(!throwing.Sync.IsBusy, "Publication exception leaked upload admission.");
    }

    private static async Task PublicFacadeSharesAdmission()
    {
        var originalGame = Current.Game;
        Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        try
        {
            var sync = ClientSnapshotComposition.Synchronization;
            sync.Reset(); Require(sync.TryBeginManual(out var held), "Shared manual admission failed.");
            var settings = new ClashOfRimSettings { ServerBaseUrl = "http://localhost", UserId = "alice", ColonyId = "colony" };
            using var cancel = new CancellationTokenSource();
            var service = new ModSnapshotUploadService(settings);
            var first = service.UploadConfiguredSnapshotAsync(cancel.Token);
            var second = service.UploadConfiguredSnapshotAsync(removeRaidBattleSessions: true, cancellationToken: cancel.Token);
            Require(!first.IsCompleted && !second.IsCompleted && sync.ManualInProgress,
                "Public facade bypassed the shared synchronization coordinator.");
            cancel.Cancel();
            await Throws<OperationCanceledException>(() => first); await Throws<OperationCanceledException>(() => second);
            Require(sync.ManualInProgress, "Public cancellation released caller-owned manual work.");
            held!.Dispose();
        }
        finally { Current.Game = originalGame; }
    }

    private static async Task PublicFacadeWithoutGame()
    {
        var originalGame = Current.Game;
        var originalState = Current.ProgramState;
        var harmony = new Harmony("ClashOfRim.Tests.SnapshotWithoutGame");
        harmony.Patch(AccessTools.Method(typeof(ClashOfRimText), "Key", new[] { typeof(string), typeof(NamedArgument[]) }),
            prefix: new HarmonyMethod(typeof(SnapshotUploaderModuleTests), nameof(Translate)));
        try
        {
            Current.Game = null;
            Current.ProgramState = ProgramState.Entry;
            foreach (string snapshot in new[] { "", "previous-snapshot" })
            foreach (bool explicitOptions in new[] { false, true })
            {
                var settings = new ClashOfRimSettings { ServerBaseUrl = "http://localhost", UserId = "alice", ColonyId = "colony",
                    CurrentSnapshotId = snapshot, CurrentLineageToken = "preserved" };
                var service = new ModSnapshotUploadService(settings);
                using var cancel = new CancellationTokenSource();
                var task = explicitOptions
                    ? service.UploadConfiguredSnapshotAsync(removeRaidBattleSessions: true, cancellationToken: cancel.Token)
                    : service.UploadConfiguredSnapshotAsync(cancel.Token);
                try
                {
                    // Only the entry pump exists without a Game. It must not need a future game to finish this request.
                    typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.MainMenu.ClashOfRimMainMenuPatches", true)!
                        .GetMethod("RunQueuedMainThreadActions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
                    Require(task.IsCompleted, "Public snapshot upload without a Game is stranded in the inactive game queue.");
                    var result = await task;
                    Require(!result.Success && result.ErrorCode == "UntrackedSaveFile"
                        && result.Message == "ClashOfRim.SnapshotUpload.NoRunningGame"
                        && !ClientSnapshotComposition.Synchronization.IsBusy
                        && settings.CurrentSnapshotId == snapshot && settings.CurrentLineageToken == "preserved",
                        "No-Game upload changed state or returned the wrong failure.");
                }
                finally
                {
                    cancel.Cancel();
                    try { await task; } catch (OperationCanceledException) { }
                    typeof(ClashOfRimGameComponent).GetMethod("RunMainThreadActions", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, null);
                }
            }
        }
        finally { Current.Game = originalGame; Current.ProgramState = originalState; harmony.UnpatchAll(harmony.Id); }
    }

    private static bool Translate(string key, ref string __result) { __result = key; return false; }

    private sealed class Fixture
    {
        internal readonly List<string> Trace = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly Store Store;
        internal readonly GameAdapter Game;
        internal readonly Transport Transport = new();
        internal readonly ClientSnapshotUploader Uploader;
        internal Fixture()
        {
            Store = new(Trace); Game = new(Trace);
            Uploader = new(Store, Game, Transport, Sync);
        }
        internal Task<ClientSnapshotUploadResult> Upload(ClientSynchronizationCoordinator.Lease? continuation = null,
            IReadOnlyList<ModSnapshotAchievementCandidateDto>? achievements = null, CancellationToken cancellation = default) =>
            Uploader.UploadAsync(true, "fixture-confirm", "fixture-kind", achievements, continuation, cancellation);
    }

    private sealed class Store : IClientSnapshotStore
    {
        private readonly List<string> trace;
        internal string User = "alice", Auth = "auth", Snapshot = "base", Token = "old-lineage";
        internal int Writes;
        internal bool ThrowOnPersist;
        internal Store(List<string> trace) => this.trace = trace;
        public ClashOfRimClientNetworkContext ReadContext() => new("http://localhost", User, "colony", Snapshot, null, null, Auth);
        public void Publish(string snapshotId, string? lineageToken) { Snapshot = snapshotId; Token = lineageToken ?? Token; trace.Add("store"); }
        public void Persist() { if (ThrowOnPersist) throw new InvalidOperationException("fixture persistence"); Writes++; trace.Add("persist"); }
    }

    private sealed record GameWork(Action Run, Action Discard);
    private sealed class GameAdapter : IClientSnapshotGame
    {
        private readonly List<string> trace;
        private readonly ConcurrentQueue<GameWork> pending = new();
        private readonly SemaphoreSlim queued = new(0);
        internal object? Owner = new();
        internal string Snapshot = "base";
        internal bool Defer, ThrowOnEnqueue, ThrowOnCapture, CancelOnCapture, Malformed, RemovedRaidSessions;
        internal int Captures;
        internal SnapshotUploadFailure? CaptureFailure;
        internal readonly List<ModSnapshotAchievementCandidateDto> Achievements = new();
        internal IReadOnlyList<ModSnapshotAchievementCandidateDto>? Published;
        internal GameAdapter(List<string> trace) => this.trace = trace;
        public IClientSnapshotGameSession BindCurrentGame() => new Session(this, Owner);
        internal async Task<GameWork> Next()
        {
            if (!await queued.WaitAsync(TimeSpan.FromSeconds(5))) throw new Exception("Game action was not queued.");
            if (!pending.TryDequeue(out var work)) throw new Exception("Game queue signal lost its work.");
            return work;
        }
        private sealed class Session : IClientSnapshotGameSession
        {
            private readonly GameAdapter game;
            private readonly object? owner;
            internal Session(GameAdapter game, object? owner) { this.game = game; this.owner = owner; }
            public bool HasGame => owner is not null;
            public bool IsCurrent => ReferenceEquals(owner, game.Owner);
            public string RuntimeSnapshotId => game.Snapshot;
            public void Enqueue(Action action, Action onDiscard)
            {
                if (game.ThrowOnEnqueue) throw new InvalidOperationException("fixture enqueue");
                var work = new GameWork(() => { if (ReferenceEquals(owner, game.Owner)) action(); else onDiscard(); }, onDiscard);
                if (game.Defer) { game.pending.Enqueue(work); game.queued.Release(); } else work.Run();
            }
            public SnapshotSaveCapture Capture(bool removeRaidBattleSessions)
            {
                game.Captures++; game.RemovedRaidSessions = removeRaidBattleSessions;
                if (game.CancelOnCapture) throw new OperationCanceledException("fixture canceled capture");
                if (game.ThrowOnCapture) throw new InvalidOperationException("fixture capture");
                string xml = game.Malformed ? "<broken" : $"<savegame><meta><gameVersion>1.7.4977</gameVersion></meta><game><components><li Class=\"AIRsLight.ClashOfRim.ClashOfRimGameComponent\"><clashOfRimLineageSnapshotId>{game.Snapshot}</clashOfRimLineageSnapshotId><clashOfRimLineageToken>old-lineage</clashOfRimLineageToken><clashOfRimPendingAchievementCandidates><li>candidate</li></clashOfRimPendingAchievementCandidates></li></components></game></savegame>";
                return new(Encoding.UTF8.GetBytes(xml), 42f, game.Achievements.ToArray(), game.CaptureFailure);
            }
            public void Publish(string snapshotId, string? lineageToken, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements)
            {
                if (!string.IsNullOrWhiteSpace(lineageToken)) game.Snapshot = snapshotId;
                game.Published = achievements; game.trace.Add("game");
            }
        }
    }

    private sealed record Upload(ClashOfRimClientNetworkContext Context, string Key, ModSnapshotPackageMetadataDto Package,
        byte[] Payload, string? Confirmation, IReadOnlyList<ModSnapshotAchievementCandidateDto> Achievements);
    private sealed class Transport : IClientSnapshotTransport
    {
        internal readonly List<Upload> Requests = new();
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>>? Hold;
        internal ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>? Failure;
        public Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(
            ClashOfRimClientNetworkContext context, string idempotencyKey, ModSnapshotPackageMetadataDto package, byte[] payload,
            string? confirmationOperation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements, CancellationToken cancellationToken)
        {
            Requests.Add(new(context, idempotencyKey, package, payload, confirmationOperation, achievements));
            Started.TrySetResult();
            return Hold?.Task ?? Task.FromResult(Failure ?? Accepted("accepted-" + Requests.Count));
        }
    }
    private static TaskCompletionSource<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> NewResponse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto> Accepted(string snapshot) =>
        ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Ok(new() { AcceptedSnapshotId = snapshot, NextLineageToken = "next", Result = new() { Accepted = true } });
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
