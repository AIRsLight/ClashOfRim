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
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientSnapshotWorldTests
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
                foreach (var purpose in Enum.GetValues<ClientSnapshotReloadPurpose>())
                foreach (string configuration in new[] { "none", "same", "changed", "blank", "no-storyteller" })
                foreach (bool presence in new[] { false, true })
                {
                    using var f = new Fixture();
                    Task? stream = presence ? f.Connection.Start(f.Reload.Ports.ReadContext(), "borrowed", 0, 0) : null;
                    await f.Start(purpose, configuration);
                    Require(!f.Source.BlocksCurrent && !f.Raids.BlocksBattleLifecycle, "Restored source/raid state stayed blocked.");
                    var expected = new List<string>();
                    if (purpose == ClientSnapshotReloadPurpose.Raid) expected.AddRange(new[] { "raid-text", "status", "message" });
                    if (configuration != "none")
                    {
                        if (configuration != "blank") expected.Add("remember");
                        expected.Add("extensions");
                        if (configuration != "no-storyteller") expected.AddRange(new[] { "story-prepare", "story-apply" });
                    }
                    expected.AddRange(new[] { "Events", "Players", "Chat" });
                    if (!presence) expected.Add("Presence");
                    expected.AddRange(new[] { "raid", "ColonySites" });
                    Require(f.Trace.SequenceEqual(expected), "Snapshot world order changed: " + string.Join(",", f.Trace));
                    Require(f.WorldId == (configuration == "changed" ? "next-world" : "world"), "Wrong remembered world.");
                    f.Connection.Stop(); if (stream is not null) await stream;
                    cases++;
                }
                foreach (string stage in new[] { "raid-text", "status", "message", "remember", "extensions", "story-prepare", "story-apply", "Events", "Players", "Chat", "Presence", "queued" })
                foreach (string change in new[] { "server", "user", "colony", "auth", "snapshot", "game", "login", "same-login", "generation", "world", "world-object" })
                {
                    using var f = new Fixture(); bool reached = false;
                    f.Hook = point => { if (point == stage) { reached = true; f.Hook = null; f.Change(change); } };
                    await f.Start();
                    Require(reached, "Test did not reach " + stage);
                    if (stage == "queued") Require(!f.Trace.Contains("ColonySites"), "Queued colony registration borrowed a new world/session: " + change);
                    else Require(f.Trace.Last() == stage, "Old completion continued after " + stage + "/" + change + ": " + string.Join(",", f.Trace));
                    cases++;
                }
                foreach (string stage in new[] { "raid-text", "extensions", "story-prepare", "Events", "queued" })
                {
                    using var f = new Fixture();
                    f.Hook = point => { if (point == stage) { f.Hook = null; throw new InvalidOperationException("controlled"); } };
                    await f.Start(gameFailure: ex => Require(ex.Message == "controlled", "Unexpected queued failure."));
                    Require(!f.Reload.Sync.IsBusy && !f.Trace.Contains("ColonySites"), "Failure retained admission or started deferred registration.");
                    cases++;
                }
                using (var f = new Fixture())
                {
                    f.Reload.ActiveRaidRecovery = new() { EventId = "restored-raid", ServerNowUtc = "bad-time" };
                    await f.Start();
                    Require(f.RaidNotices.Single().State == ClientRaidRecoveryState.MissingBattle
                        && f.RaidNotices.Single().EventId == "restored-raid" && f.Raids.BlocksBattleLifecycle,
                        "Snapshot recovery metadata did not reach the real raid recovery module.");
                    cases++;
                }
                using (var f = new Fixture())
                {
                    f.Ready = false; await f.Start();
                    Require(f.Raids.BlocksBattleLifecycle && !f.Trace.Contains("raid"), "Unready game skipped raid readiness.");
                    f.Change("same-login"); f.Ready = true; f.Raids.Advance();
                    Require(!f.Trace.Contains("raid"), "Deferred raid recovery rebound after login."); cases++;
                }
            }
            Console.WriteLine($"PASS: snapshot world continuation ({cases} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }

    private sealed class Fixture : IClientWorldSourceGame, IClientWorldIdentity, IClientWorldConfigurationRuntime,
        IClientWorldConfigurationStore, IClientStorytellerRuntime, IClientSnapshotWorldPresentation, IClientSessionFollowups,
        IClientRaidRecoveryRuntime, IClientRaidRecoveryPresentation, IClientSessionStreamTransport, IDisposable
    {
        internal readonly ClientSnapshotReloadTestPorts Reload = new();
        internal readonly ClientWorldSourceGame Source;
        internal readonly ClientRaidRecovery Raids;
        internal readonly ClientSessionConnection Connection;
        internal readonly List<string> Trace = new();
        internal readonly List<ClientRaidRecoveryNotice> RaidNotices = new();
        internal Action<string>? Hook;
        internal object World = new();
        public string WorldId { get; private set; } = "world";
        public object? Current => Reload.Ports.GameOwner;
        public bool Ready { get; set; } = true;
        internal Fixture()
        {
            Source = new(this); Connection = new(this);
            var storyteller = new ClientStorytellerConfiguration(Reload.Session, new(Reload.Session), this);
            var configuration = new ClientWorldConfiguration(Reload.Session, this, this, storyteller, null!);
            Raids = new(Reload.Session, Reload.Ports, new(Reload.Session, Reload.Mutations, Reload.Sync, Reload.Ports, Reload.View), null!, this);
            var workflow = new ClientSnapshotWorld(Source, configuration, this, this, Connection, this, Raids, this, this);
            Reload.Module.Completed += completed => { Source.BlockCurrent(); workflow.Continue(completed); };
            Reload.Ports.BeforeGameAction = () => Hook?.Invoke("queued");
        }
        internal Task Start(ClientSnapshotReloadPurpose purpose = ClientSnapshotReloadPurpose.Raid, string configuration = "same", Action<Exception>? gameFailure = null)
        {
            var catalog = configuration == "none" ? null : new ModWorldConfigurationDto
            {
                WorldConfigurationId = configuration == "changed" ? " next-world " : configuration == "blank" ? " " : "world",
                StorytellerDefName = configuration == "no-storyteller" ? null : "Cassandra", DifficultyDefName = "Rough",
                DifficultyValuesXml = "<difficulty><threatScale>1.75</threatScale></difficulty>"
            };
            return Reload.Ports.PumpUntil(Reload.Module.StartAsync(new("base", purpose, catalog)), gameFailure);
        }
        internal void Change(string change)
        {
            if (change == "world") WorldId = "unrelated";
            else if (change == "world-object") World = new();
            else Reload.Change(change);
        }
        private void Hit(string stage) { Trace.Add(stage); Hook?.Invoke(stage); }
        public void Remember(string world) { WorldId = world; Hit("remember"); }
        public IClientWorldConfigurationSession Bind() => new Bound(this, Reload.Ports.GameOwner, World);
        IClientStorytellerSession IClientStorytellerRuntime.Bind() => new Bound(this, Reload.Ports.GameOwner, World);
        public string RaidRestored(string snapshot) { Require(snapshot == Reload.SnapshotId, "Wrong restored snapshot message."); Hit("raid-text"); return CultureInfo.CurrentCulture.Name + ":" + snapshot; }
        public void Status(string text) { Require(text == CultureInfo.CurrentCulture.Name + ":" + Reload.SnapshotId, "Wrong localized status."); Hit("status"); }
        public void Message(string text) => Hit("message");
        public void AdoptQueue(ModEventQueueSummaryDto? queue) => throw new NotSupportedException();
        public Task Dispatch(ClientSessionFollowup effect, ClientSessionFollowupSource source, Func<Action, bool> step)
        {
            Require(source == ClientSessionFollowupSource.SnapshotReload && !Reload.Sync.IsBusy, "Wrong source or retained reload admission.");
            step(() => Hit(effect.ToString())); return Task.CompletedTask;
        }
        public IClientRaidRecoveredBattle? ReadBattle() { Hit("raid"); return null; }
        public string Format(ClientRaidRecoveryNotice notice) => notice.State.ToString();
        public void Report(ClientRaidRecoveryNotice notice, string message) => RaidNotices.Add(notice);
        public async Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(ClashOfRimClientNetworkContext context, string id, long version, long world,
            Action<ModSessionStreamEvent> onEvent, CancellationToken token)
        { await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token); throw new InvalidOperationException(); }
        public void Dispose() { Connection.Stop(); Reload.Dispose(); }
        private sealed record Bound(Fixture F, object Game, object World) : IClientWorldConfigurationSession, IClientStorytellerSession, IClientStorytellerChange
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Reload.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public bool IsPlaying => true;
            public ClientWorldFeatureNames? ApplyFeatures(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public void ApplyFactions(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public void ApplyRoads(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public void ApplyObjectLabels(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public void ApplyExtensions(ModWorldConfigurationDto configuration, string user, bool state)
            { Require(user == "alice" && !state, "Reload changed catalog projection semantics."); F.Hit("extensions"); }
            public ClientStorytellerBaseline Capture() => throw new NotSupportedException();
            public IClientStorytellerChange? Prepare(ClientStorytellerBaseline baseline)
            {
                Require(baseline.Storyteller == "Cassandra" && baseline.Difficulty == "Rough" && baseline.ValuesXml!.Contains("1.75"), "Difficulty baseline changed.");
                F.Hit("story-prepare"); return this;
            }
            public void Apply() => F.Hit("story-apply");
            public void LockCommitment() => throw new NotSupportedException();
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
