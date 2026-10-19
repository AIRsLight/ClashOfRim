using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Achievements;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Achievements;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using HarmonyLib;
using Verse;

internal static class ClientEndgameAchievementTests
{
    internal static void Run() { try { RunAsync().GetAwaiter().GetResult(); } catch (Exception ex) { Console.WriteLine(ex); throw; } }
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Queue(); Persistence(); Dialog(); Catalog(); count += 4;
                foreach (string inactive in new[] { "user", "session", "snapshot", "game", "observation", "unavailable", "queue" })
                { await Inactive(inactive); count++; }
                await Success(); await FailedAndRetry(); await Busy(); await Reentry(); count += 4;
                foreach (string change in new[] { "user", "colony", "server", "auth", "game", "login", "generation" })
                {
                    OldDialog(change); count++;
                    foreach (string phase in new[] { "capture", "send", "publish", "complete" })
                    { await Late(change, phase); count++; }
                }
            }
            SkippedOriginal(); count++;
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); Scribe.ForceStop(); }
        Console.WriteLine($"PASS: endgame achievements ({count} scenarios)");
    }
    private static ModSnapshotAchievementCandidateDto Award(string id = "a", string key = "event")
        => new() { AchievementId = id, EventKey = key, Value = 1, Category = "Endgame", LabelKey = "标签", IconId = "icon", Color = "Red", AggregationKind = "Sum" };
    private static void Catalog()
    {
        var cases = new[]
        {
            (ClientEndgameKind.Ship, "endgame_ship_launch", "EndgameShipLaunch", "endgame.ship.launch"),
            (ClientEndgameKind.Archonexus, "endgame_archonexus", "EndgameArchonexus", "endgame.archonexus"),
            (ClientEndgameKind.VoidEmbrace, "endgame_void_embrace", "EndgameVoid", "endgame.void"),
            (ClientEndgameKind.VoidDisrupt, "endgame_void_disrupt", "EndgameVoid", "endgame.void"),
            (ClientEndgameKind.CerebrexScavenged, "endgame_cerebrex_scavenged", "EndgameCerebrex", "endgame.cerebrex"),
            (ClientEndgameKind.CerebrexDestroyed, "endgame_cerebrex_destroyed", "EndgameCerebrex", "endgame.cerebrex")
        };
        foreach (var (kind, id, label, icon) in cases)
        {
            var candidate = ClientEndgameAchievements.Candidate(kind, "event:123");
            Check(candidate.AchievementId == id && candidate.LabelKey == "ClashOfRim.Achievement." + label && candidate.IconId == icon
                && candidate.Color == (kind == ClientEndgameKind.Ship ? "Purple" : "Red") && candidate.Value == 1
                && candidate.Category == "Endgame" && candidate.AggregationKind == "Sum"
                && candidate.EventKey == (kind == ClientEndgameKind.Ship ? "event:123:launch" : "event:123"), "Endgame catalog changed: " + kind);
        }
    }
    private static void Queue()
    {
        var q = new ClientAchievementCandidates();
        var original = Award(" a:b ", " c "); original.Value = long.MaxValue; original.MetadataJson = "{文化 İ العربية}";
        var added = q.Enqueue(new[] { original, Award("a", "b:c"), Award("a:b", "c"), Award("A:B", "c"), null!, Award("", "") });
        Check(added.Count == 3 && q.Count == 3, "Queue merged different identities or ignored ordinal/trim rules.");
        original.Value = -1; added[0].Value = -2; var copied = q.Snapshot(); copied[0].Value = -3;
        Check(q.Snapshot()[0].Value == long.MaxValue && q.Snapshot()[0].MetadataJson == "{文化 İ العربية}", "Queue aliases its inputs/outputs.");
        var reopened = new ClientAchievementCandidates(); reopened.Restore(q.Snapshot().Concat(q.Snapshot()));
        Check(reopened.Count == 3 && reopened.Snapshot()[0].Category == "Endgame" && reopened.Snapshot()[0].AggregationKind == "Sum", "Queue restore lost metadata/deduplication.");
        reopened.Acknowledge(new[] { Award(" a:b", "c "), null!, Award("", "") });
        Check(reopened.Count == 2 && reopened.Snapshot().Any(c => c.AchievementId == "a" && c.EventKey == "b:c"), "Acknowledgment deleted another identity.");
        reopened.Acknowledge(new[] { Award("a:b", "c") }); Check(reopened.Count == 2, "Duplicate acknowledgment changed queue.");
        reopened.Restore(null); Check(reopened.Count == 0, "Missing legacy queue did not restore empty.");
    }
    private static bool QuietPrefs(ref bool __result) { __result = false; return false; }
    private static void Persistence()
    {
        var harmony = new Harmony("ClashOfRim.Tests.EndgameScribe");
        harmony.Patch(AccessTools.PropertyGetter(typeof(Prefs), "LogVerbose"), prefix: new HarmonyMethod(typeof(ClientEndgameAchievementTests), nameof(QuietPrefs)));
        var saved = new PendingAchievementCandidates();
        saved.Queue.Enqueue(new[] { Award("a:b", "c"), Award("a", "b:c") });
        string xml = Scribe.saver.DebugOutputFor(saved);
        Check(XElement.Parse(xml).Element("clashOfRimPendingAchievementCandidates")!.Elements("li").Count() == 2, "Original queue save path changed.");
        string path = Path.Combine(Path.GetTempPath(), "clash-endgame-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            // Old saves use the same record fields directly under the GameComponent.
            string fields = string.Concat(XElement.Parse(xml).Elements().Select(e => e.ToString()));
            string legacy = "<clashOfRimPendingAchievementCandidates><li><achievementId>old-award</achievementId><eventKey>old:event</eventKey><value>9223372036854775806</value><category>Endgame</category><labelKey>标签</labelKey><iconId>icon</iconId><color>Purple</color><aggregationKind>Sum</aggregationKind><metadataJson>legacy metadata</metadataJson></li></clashOfRimPendingAchievementCandidates>";
            foreach (string contents in new[] { fields, legacy, "" })
            {
                File.WriteAllText(path, "<root><queue>" + contents + "</queue></root>");
                PendingAchievementCandidates? loaded = null;
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref loaded, "queue");
                Scribe.loader.FinalizeLoading();
                Check(loaded is not null && loaded.Queue.Count == (contents == "" ? 0 : contents == legacy ? 1 : 2), "Legacy Scribe queue did not restore after loading phases.");
                if (contents == legacy) Check(loaded!.Queue.Snapshot().Single().Value == long.MaxValue - 1 && loaded.Queue.Snapshot()[0].MetadataJson == "legacy metadata", "Old literal save lost values or metadata.");
                if (contents == fields) Check(loaded!.Queue.Snapshot()[1].EventKey == "b:c" && loaded.Queue.Snapshot()[0].LabelKey == "标签", "Scribe lost identity/metadata.");
            }
        }
        finally { Scribe.ForceStop(); File.Delete(path); harmony.UnpatchAll(harmony.Id); }
    }
    private static void Dialog()
    {
        using var f = new Fixture(); int runs = 0;
        Action continuation = () => { Check(f.Module.ConfirmOrDefer("void", () => throw new Exception("extra dialog")), "Confirmation did not allow synchronous original reentry."); runs++; };
        Check(!f.Module.ConfirmOrDefer("void", continuation) && f.Dialogs.Count == 1, "Endgame was not deferred.");
        var accept = f.Dialogs[0]; accept(); accept(); Check(runs == 1, "Dialog accepted twice.");
        Check(!f.Module.ConfirmOrDefer("void", continuation), "Approval leaked past continuation.");
        f.Module.ConfirmOrDefer("throw", () => throw new InvalidOperationException("game failure"));
        try { f.Dialogs.Last()(); } catch (InvalidOperationException) { }
        Check(!f.Module.ConfirmOrDefer("throw", () => { }), "Failed continuation retained a global permit.");
        f.Module.ConfirmOrDefer("no-reentry", () => { }); f.Dialogs.Last()();
        Check(!f.Module.ConfirmOrDefer("no-reentry", () => { }), "Unconsumed permit survived callback return.");
        f.Module.ConfirmOrDefer("normal-snapshot", () => runs++); var normal = f.Dialogs.Last(); f.Ports.Snapshot = "advanced"; normal();
        Check(runs == 2, "Ordinary snapshot progression invalidated a pending gameplay decision.");
    }
    private static void OldDialog(string change)
    {
        using var f = new Fixture(); int runs = 0;
        f.Module.ConfirmOrDefer("void", () => runs++); var old = f.Dialogs.Single(); f.Change(change); old();
        Check(runs == 0, "Old dialog crossed " + change);
    }
    private static async Task Inactive(string reason)
    {
        using var f = new Fixture();
        switch (reason)
        {
            case "user": f.Ports.User = ""; break;
            case "session": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break;
            case "snapshot": f.Ports.Snapshot = ""; break;
            case "game": f.Ports.HasGame = false; break;
            case "observation": f.Observing = true; break;
            case "unavailable": f.Session.BeginUnavailable("auth", "session"); break;
            case "queue": f.NoQueue = true; break;
        }
        if (reason != "queue") Check(f.Module.ConfirmOrDefer("key", () => { }) && f.Dialogs.Count == 0, "Inactive game acquired confirmation.");
        await f.Module.SubmitAsync(new[] { Award() }); Check(f.Pending.Count == 0 && f.Uploads == 0, "Inactive endgame captured awards: " + reason);
    }
    private static async Task Success()
    {
        using var f = new Fixture();
        f.OnSend = () => f.Pending.Enqueue(new[] { Award("late", "after-capture") });
        await f.Ports.PumpUntil(f.Module.SubmitAsync(new[] { Award("endgame_ship_launch", "ship:1:launch") }));
        Check(f.Uploads == 1 && f.Confirmed == 1 && f.WireOperation == "EndgameAchievement" && f.WireKind == ModSnapshotUploadKinds.EndgameAchievement
            && f.Sent.Single().AchievementId == "endgame_ship_launch" && !f.RemovedRaid && f.Ports.GamePublished == 1, "Endgame upload lost classification/publication.");
        Check(f.Pending.Snapshot().Single().AchievementId == "late" && !f.Mutations.IsPending && !f.Sync.IsBusy
            && f.Status == "ClashOfRim.Endgame.UploadSucceeded", "Endgame completion deleted an uncaptured candidate or leaked admission.");
    }
    private static async Task FailedAndRetry()
    {
        using var f = new Fixture { Fail = true };
        await f.Ports.PumpUntil(f.Module.SubmitAsync(new[] { Award() }));
        Check(f.Pending.Count == 1 && f.Mutations.Current?.Kind == LocalMutationKind.Endgame && f.View.Retry is not null
            && f.Confirmed == 0 && !f.Sync.IsBusy && f.Status.StartsWith("ClashOfRim.Endgame.UploadFailed", StringComparison.Ordinal), "Failed endgame lost recovery/queue.");
        f.Fail = false; var retry = f.View.Retry!; retry(); retry();
        await f.Ports.PumpUntil(f.Done.Task);
        Check(f.Uploads == 2 && f.Confirmed == 1 && f.Pending.Count == 0 && !f.Mutations.IsPending && !f.Sync.IsBusy
            && f.WireKind == ModSnapshotUploadKinds.EndgameAchievement && f.WireOperation == "EndgameAchievement", "Retry repeated gameplay or changed endgame classification.");
    }
    private static async Task Busy()
    {
        foreach (bool otherMutation in new[] { false, true })
        {
            using var f = new Fixture();
            ClientSynchronizationCoordinator.Lease? lease = null;
            if (otherMutation) f.Mutations.Begin(LocalMutationKind.Bank, "bank"); else f.Sync.TryBeginManual(out lease);
            await f.Module.SubmitAsync(new[] { Award() });
            Check(f.Pending.Count == 1 && f.Uploads == 0 && f.Confirmed == 0 && (otherMutation ? f.Mutations.Current?.Kind == LocalMutationKind.Bank : lease!.IsCurrent), "Busy endgame lost queued award or released another operation.");
            lease?.Dispose();
        }
    }
    private static async Task Reentry()
    {
        foreach (string change in new[] { "game", "observation", "unavailable" })
        {
            using var f = new Fixture();
            f.OnText = () =>
            {
                if (change == "observation") f.Observing = true;
                else if (change == "unavailable") f.Session.BeginUnavailable("auth", "session");
                else f.Change(change);
            };
            await f.Module.SubmitAsync(new[] { Award() });
            Check(f.Uploads == 0 && !f.Sync.IsBusy, "Status formatter admitted an inactive endgame: " + change);
        }
    }
    private static async Task Late(string change, string phase)
    {
        using var f = new Fixture();
        if (phase == "capture") f.Ports.OnCapture = () => f.Change(change);
        if (phase == "send") f.OnSend = () => f.Change(change);
        if (phase is "publish" or "complete")
        {
            int actions = 0; int at = phase == "publish" ? 2 : 3;
            f.Ports.BeforeGameAction = () => { if (++actions == at) f.Change(change); };
        }
        await f.Ports.PumpUntil(f.Module.SubmitAsync(new[] { Award() }));
        Check(f.Confirmed == 0 && !f.Sync.IsBusy && f.Messages == 0 && f.View.Shown == 0, "Late endgame completion escaped ownership: " + change + "/" + phase);
        if (phase != "complete") Check(f.Pending.Count == 1 && f.Ports.Published == 0, "Stale endgame consumed its queue.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void SkippedEndgame() => throw new Exception("Skipped original ran.");
    private static bool Skip() => false;
    private static readonly List<(ClientEndgameKind Kind, string Key)> observed = new();
    private static bool CaptureAward(ClientEndgameKind kind, string eventKey) { observed.Add((kind, eventKey)); return false; }
    private static bool FixedTick(ref int __result) { __result = 123; return false; }
    private static void SkippedOriginal()
    {
        var harmony = new Harmony("ClashOfRim.Tests.EndgameSkippedOriginal");
        var original = typeof(ClientEndgameAchievementTests).GetMethod(nameof(SkippedEndgame), BindingFlags.NonPublic | BindingFlags.Static)!;
        observed.Clear();
        try
        {
            harmony.Patch(AccessTools.Method(typeof(EndgameAchievementRuntime), "Confirm"), prefix: new HarmonyMethod(typeof(ClientEndgameAchievementTests), nameof(CaptureAward)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(GenTicks), "TicksGame"), prefix: new HarmonyMethod(typeof(ClientEndgameAchievementTests), nameof(FixedTick)));
            foreach (Type patch in new[] { typeof(ClashOfRimEmbraceTheVoidPatch), typeof(ClashOfRimDisruptTheLinkPatch), typeof(ClashOfRimArchonexusEndGamePatch) })
            {
                harmony.Patch(original, prefix: new HarmonyMethod(typeof(ClientEndgameAchievementTests), nameof(Skip)), postfix: new HarmonyMethod(patch, "Postfix"));
                SkippedEndgame(); harmony.Unpatch(original, HarmonyPatchType.All, harmony.Id);
            }
            ClashOfRimShipCountdownEndedPatch.Postfix(false, "ship:123");
            ClashOfRimCerebrexDeactivateCorePatch.Postfix(false, false);
            Check(observed.Count == 0, "A skipped original emitted an endgame award.");
            ClashOfRimEmbraceTheVoidPatch.Postfix(true); ClashOfRimDisruptTheLinkPatch.Postfix(true);
            ClashOfRimArchonexusEndGamePatch.Postfix(true); ClashOfRimCerebrexDeactivateCorePatch.Postfix(true, true);
            ClashOfRimCerebrexDeactivateCorePatch.Postfix(false, true); ClashOfRimShipCountdownEndedPatch.Postfix(true, "ship:123:root");
            Check(observed.SequenceEqual(new[] { (ClientEndgameKind.VoidEmbrace, "void-embrace:123"), (ClientEndgameKind.VoidDisrupt, "void-disrupt:123"),
                (ClientEndgameKind.Archonexus, "archonexus:123"), (ClientEndgameKind.CerebrexScavenged, "cerebrex:scavenged:123"),
                (ClientEndgameKind.CerebrexDestroyed, "cerebrex:destroyed:123"), (ClientEndgameKind.Ship, "ship:123:root") }), "Endgame patch mapping/event identity changed.");
        }
        finally { harmony.UnpatchAll(harmony.Id); observed.Clear(); }
    }

    internal sealed class Fixture : IDisposable, IClientSnapshotGame, IClientSnapshotTransport, IClientLocalSnapshotPresentation, IClientEndgameRuntime, IClientEndgamePresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly EventModuleFailureView View;
        internal readonly ClientEndgameAchievements Module;
        internal readonly List<Action> Dialogs = new();
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Observing, NoQueue, Fail, RemovedRaid;
        internal Action? OnSend, OnText;
        internal int Uploads, Confirmed, Messages;
        internal string Status = "";
        internal string? WireKind, WireOperation;
        internal IReadOnlyList<ModSnapshotAchievementCandidateDto> Sent = Array.Empty<ModSnapshotAchievementCandidateDto>();
        internal ClientAchievementCandidates Pending { get; } = new();
        ClientAchievementCandidates? IClientEndgameRuntime.Pending => NoQueue ? null : Pending;
        public bool IsObserving => Observing;
        internal Fixture(bool reset = true)
        {
            if (reset) ClientSessionWork.Reset();
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "session" });
            View = new(Ports);
            var transactions = new ClientSnapshotTransaction(Session, this, new ClientSnapshotPublication(Ports, this));
            var confirmation = new ClientLocalSnapshotConfirmation(Session, this, new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions),
                Sync, Mutations, transactions, new ClientSnapshotFailureRecovery(Session, Mutations, Sync, this, View), new ClientSnapshotUploader(Ports, this, this, Sync), this);
            Module = new(Session, this, this, this, confirmation);
            Module.Confirmed += () => { Confirmed++; Done.TrySetResult(); };
        }
        public void Confirm(Action accept) => Dialogs.Add(accept);
        public string Text(string key, params (string Name, object? Value)[] arguments) { OnText?.Invoke(); return key + string.Join("", arguments.Select(a => ":" + a.Value)); }
        public IClientSnapshotGameSession BindCurrentGame() => new Game(this, Ports.BindCurrentGame());
        private sealed record Game(Fixture F, IClientSnapshotGameSession Inner) : IClientSnapshotGameSession
        {
            public bool HasGame => Inner.HasGame;
            public bool IsCurrent => Inner.IsCurrent;
            public string RuntimeSnapshotId => Inner.RuntimeSnapshotId;
            public void Enqueue(Action action, Action discard) => Inner.Enqueue(action, discard);
            public SnapshotSaveCapture Capture(bool removeRaidBattleSessions)
            { F.RemovedRaid = removeRaidBattleSessions; return Inner.Capture(false) with { Achievements = F.Pending.Snapshot() }; }
            public void Publish(string snapshot, string? token, IReadOnlyList<ModSnapshotAchievementCandidateDto> candidates)
            { F.Pending.Acknowledge(candidates); Inner.Publish(snapshot, token, Array.Empty<ModSnapshotAchievementCandidateDto>()); }
        }
        public Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string key,
            ModSnapshotPackageMetadataDto package, byte[] payload, string? operation, IReadOnlyList<ModSnapshotAchievementCandidateDto> candidates, CancellationToken token)
        {
            Uploads++; WireKind = package.SnapshotUploadKind; WireOperation = operation; Sent = candidates; OnSend?.Invoke();
            return Task.FromResult(Fail ? ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Failed("HttpError", "503 unavailable")
                : ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Ok(new() { Result = new() { Accepted = true }, AcceptedSnapshotId = "accepted", NextLineageToken = "lineage" }));
        }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "colony": Ports.Colony = "other"; break;
                case "server": Ports.Server = "http://other"; break; case "auth": Ports.Auth = "other"; break;
                case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "session" }); break;
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); Mutations.Reset(); break;
            }
        }
        public string Busy => "busy";
        public string BaseSnapshotChanged => "base-changed";
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public ModSnapshotUploadResult Result(ClientSnapshotUploadResult result) => result.Failure is { } failure
            ? ModSnapshotUploadResult.Failed(failure.Code, failure.Message ?? "fallback") : ModSnapshotUploadResult.Ok(result.AcceptedSnapshotId!, result.SourcePath!);
        public string Failed(string status, string? code, string? reason) => "failed";
        public string Succeeded(string? snapshotId) => "success";
        public string Exception(string status, Exception exception) => "exception";
        public void DefaultStatus(string message) => Status = message;
        public void Message(string message, bool accepted, bool bestEffort) => Messages++;
        public void Diagnose(LocalMutationKind operation, string message, Exception? exception) { }
        public void Dispose() { Sync.Reset(); ClientSessionWork.Reset(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
