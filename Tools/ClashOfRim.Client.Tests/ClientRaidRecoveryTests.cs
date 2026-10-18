using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientRaidRecoveryTests
{
    internal static void Run()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (int seconds in new[] { -1, 0, 1, 119, 120, 121 })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                using var f = new Fixture(); var data = Data(seconds);
                f.Module.Reconcile(f, data); f.Module.Advance(); f.Module.Advance();
                Check(f.Battle.Restores == 1 && f.Ports.Queued == 0 && !f.Sync.IsBusy
                    && !f.Module.BlocksBattleLifecycle
                    && f.Battle.Times!.DeadlineUtcTicks == DateTimeOffset.Parse("2026-10-18T08:00:00Z", CultureInfo.InvariantCulture).UtcTicks,
                    "Recovery repeated, queued recursively, or converted culture/time incorrectly.");
                Check(f.Settlements == (seconds is >= 0 and < 120 ? 1 : 0) && f.Expired == (seconds >= 120 ? 1 : 0)
                    && f.Notices.Count(n => n.State == ClientRaidRecoveryState.Restored) == (seconds < 0 ? 1 : 0), "Deadline boundary chose the wrong action.");
                if (seconds < 0) Check(f.Notices.Last().RemainingSeconds == 1, "Remaining time rounded incorrectly.");
            }
            foreach (bool expired in new[] { false, true })
            {
                using var f = new Fixture();
                f.Module.Reconcile(f, expired ? Data(120) : null, authoritativeReload: true);
                f.Module.Advance();
                Check(f.Reloads == 0 && f.Expired == 0 && f.Settlements == 0 && f.Battle.Restores == 0
                    && f.Module.BlocksBattleLifecycle && f.Notices.Single().State == ClientRaidRecoveryState.ServerUnresolved,
                    "An unresolved authoritative snapshot recursively reloaded the same battle.");
            }
            using (var f = new Fixture { IsReady = false })
            {
                var data = Data(-60); f.Module.Reconcile(f, data);
                data.EventId = "changed"; data.ServerNowUtc = "invalid";
                for (int i = 0; i < 100; i++) f.Module.Advance();
                Check(f.BattleReads == 0 && f.Ports.Queued == 0 && f.Notices.Count == 1 && f.Battle.Restores == 0 && f.Module.BlocksBattleLifecycle,
                    "Readiness wait queued, repeated effects/logging, or allowed automatic deadlines.");
                f.IsReady = true; f.Module.Advance(); f.Module.Advance();
                Check(f.Battle.Restores == 1 && f.Notices.Last().RemainingSeconds == 60, "Queued recovery aliases mutable DTO or repeated.");
            }
            foreach (string change in new[] { "server", "auth", "user", "colony", "snapshot", "game", "login", "generation" })
            {
                using var f = new Fixture { IsReady = false };
                f.Module.Reconcile(f, Data(1)); f.Change(change); f.IsReady = true; f.Module.Advance();
                Check(f.Battle.Restores == 0 && f.Settlements == 0 && f.Expired == 0 && f.Reloads == 0 && f.Ports.Queued == 0,
                    "Pending recovery affected another owner: " + change);
                using var after = new Fixture(); after.OnRestore = () => after.Change(change);
                after.Module.Reconcile(after, Data(1));
                Check(after.Battle.Restores == 1 && after.Settlements == 0 && after.Expired == 0, "Restore continuation ignored changed owner: " + change);
            }
            foreach (string invalid in new[] { "missing", "event", "map", "user", "colony", "current", "now", "started", "deadline", "final", "exception" })
            {
                using var f = new Fixture(); var data = Data(-60);
                switch (invalid)
                {
                    case "missing": f.NoBattle = true; break; case "event": data.EventId = "other"; break; case "map": f.Battle.HasMap = false; break;
                    case "user": f.Battle.Identity = f.Battle.Identity with { AttackerUserId = "other" }; break;
                    case "colony": f.Battle.Identity = f.Battle.Identity with { AttackerColonyId = "other" }; break;
                    case "current": f.Battle.IsCurrent = false; break; case "now": data.ServerNowUtc = "invalid"; break;
                    case "started": data.StartedAtUtc = "invalid"; break; case "deadline": data.DeadlineUtc = "invalid"; break;
                    case "final": data.FinalDeadlineUtc = "invalid"; break; case "exception": f.OnRestore = () => throw new InvalidOperationException("restore"); break;
                }
                f.Module.Reconcile(f, data); f.Ports.PumpUntil(Task.CompletedTask).GetAwaiter().GetResult();
                Check(f.View.Visible is not null && f.View.Retry is null && f.Mutations.Current?.Kind == LocalMutationKind.RaidRestore
                    && f.Module.BlocksBattleLifecycle
                    && f.Settlements == 0 && f.Expired == 0 && f.Reloads == 0, "Invalid recovery mutated battle or allowed unsafe retry: " + invalid);
                Check(f.Battle.Restores == (invalid == "exception" ? 1 : 0), "Invalid response partially applied deadlines.");
            }
            using (var f = new Fixture { NoBattle = true })
            {
                f.Module.Reconcile(f, null); Check(f.Notices.Count == 0 && f.Reloads == 0, "Absent server and local raid triggered recovery.");
                f.NoBattle = false; f.Module.Reconcile(f, null); f.Module.Advance();
                Check(f.Reloads == 1 && f.Battle.Restores == 0 && f.Settlements == 0 && f.Expired == 0 && f.Module.BlocksBattleLifecycle,
                    "Already settled raid lost authoritative reload signal or allowed automatic deadlines before reload.");
            }
            using (var f = new Fixture { IsReady = false })
            {
                f.Module.Reconcile(f, Data(-60)); f.Module.Reconcile(f, Data(1)); f.IsReady = true; f.Module.Advance();
                Check(f.Settlements == 1 && f.Battle.Restores == 1, "Newer recovery did not supersede pending data.");
            }
            using (var f = new Fixture())
            {
                f.Ports.HasGame = false; f.Module.Reconcile(f, Data(1)); f.Ports.HasGame = true; f.Module.Advance();
                Check(f.BattleReads == 0 && f.Settlements == 0, "Recovery without a game attached to a future game.");
            }
            using (var f = new Fixture())
            {
                var data = Data(-60); data.DeadlineUtc = "9999-01-01T00:00:00Z"; data.FinalDeadlineUtc = "9999-12-31T00:00:00Z";
                f.Module.Reconcile(f, data); Check(f.Notices.Last().RemainingSeconds == int.MaxValue, "Long server time overflowed remaining seconds.");
            }
            using (var f = new Fixture { IsReady = false })
            {
                f.Module.Reconcile(f, Data(1));
                var mod = (AIRsLight.ClashOfRim.ClashOfRimMod)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AIRsLight.ClashOfRim.ClashOfRimMod));
                HarmonyLib.AccessTools.Field(typeof(AIRsLight.ClashOfRim.ClashOfRimMod), "raidRecovery").SetValue(mod, f.Module);
                var component = new AIRsLight.ClashOfRim.ClashOfRimGameComponent(null!);
                HarmonyLib.AccessTools.Field(component.GetType(), "activeRaidBattleSession").SetValue(component,
                    new AIRsLight.ClashOfRim.Raids.ActiveRaidBattleSession { EventId = "raid", DeadlineUtcTicks = 1, FinalDeadlineUtcTicks = 1 });
                HarmonyLib.AccessTools.Method(component.GetType(), "TryFinishExpiredRaidBattle").Invoke(component, new object[] { mod });
                HarmonyLib.AccessTools.Method(component.GetType(), "TryHandleRaidBattleFinalDeadline").Invoke(component, new object[] { mod });
                Check(f.Module.BlocksBattleLifecycle, "Engine callbacks bypassed pending recovery.");
            }
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private static ModActiveRaidRecoveryDto Data(int seconds) => new()
    {
        EventId = "raid", StartedAtUtc = "2026-10-18T15:45:00+08:00", DeadlineUtc = "2026-10-18T16:00:00+08:00",
        FinalDeadlineUtc = "2026-10-18T16:02:00+08:00", ServerNowUtc = new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero).AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture)
    };
    private sealed class Battle : IClientRaidRecoveredBattle
    {
        private readonly Fixture fixture;
        internal Battle(Fixture fixture) => this.fixture = fixture;
        public ClientRaidSettlementIdentity Identity { get; set; } = new("raid", "alice", "colony", "42");
        public bool IsCurrent { get; set; } = true;
        public bool HasMap { get; set; } = true;
        internal int Restores;
        internal ClientRaidServerDeadlines? Times;
        public void RestoreDeadlines(ClientRaidServerDeadlines deadlines) { Restores++; Times = deadlines; fixture.OnRestore?.Invoke(); }
        public TimeSpan FinalRemaining => throw new NotSupportedException();
        public bool TryBegin() => throw new NotSupportedException();
        public void KeepFinish() => throw new NotSupportedException();
        public void ResetFinish() => throw new NotSupportedException();
        public IClientRaidSupportLossSource SupportLosses => throw new NotSupportedException();
        public void EnqueueSaving(Action action, Action discarded) => throw new NotSupportedException();
        public void CloseAccepted(string reason) => throw new NotSupportedException();
        public void CloseExpired(bool closeLocalMap) => throw new NotSupportedException();
    }
    private sealed class Fixture : IClientRaidRecoveryRuntime, IClientRaidSettlementActions, IClientRaidRecoveryPresentation, IDisposable
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly EventModuleFailureView View;
        internal readonly ClientRaidRecovery Module;
        internal readonly Battle Battle;
        internal readonly List<ClientRaidRecoveryNotice> Notices = new();
        internal int BattleReads, Settlements, Expired, Reloads;
        internal bool IsReady = true, NoBattle;
        internal Action? OnRestore;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports)); View = new(Ports); Battle = new(this);
            Module = new(Session, Ports, new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, View), this, this);
            Module.ReloadRequested += id => { Check(id == "raid", "Reload changed event identity."); Reloads++; };
        }
        public bool Ready => IsReady;
        public IClientRaidRecoveredBattle? ReadBattle() { BattleReads++; return NoBattle ? null : Battle; }
        public Task StartAsync(IClientRaidSettlementRuntime runtime, string reason)
        { Check(ReferenceEquals(runtime, Battle) && reason == "RecoveredExpired" && Battle.Restores == 1, "Settlement used wrong runtime/reason/order."); Settlements++; return Task.CompletedTask; }
        public void Expire(IClientRaidSettlementRuntime runtime, bool closeLocalMap = true)
        { Check(ReferenceEquals(runtime, Battle) && closeLocalMap && Battle.Restores == 1, "Expiry used wrong runtime/order."); Expired++; }
        public string Format(ClientRaidRecoveryNotice notice) => notice.State.ToString();
        public void Report(ClientRaidRecoveryNotice notice, string message) => Notices.Add(notice);
        internal void Change(string change)
        {
            switch (change)
            {
                case "server": Ports.Server = "http://other"; break; case "auth": Ports.Auth = "other"; break;
                case "user": Ports.User = "other"; break; case "colony": Ports.Colony = "other"; break;
                case "snapshot": Ports.Snapshot = "other"; break; case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break;
                case "generation": ClientSessionWork.Reset(); break;
            }
        }
        public void Dispose() { Sync.Reset(); ClientSessionWork.Reset(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
