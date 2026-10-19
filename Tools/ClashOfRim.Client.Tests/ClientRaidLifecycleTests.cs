using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Raids;
using HarmonyLib;
using Verse;

internal static class ClientRaidLifecycleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        foreach (bool current in new[] { false, true })
        foreach (bool expired in new[] { false, true })
        foreach (bool finishing in new[] { false, true })
        foreach (bool final in new[] { false, true })
        foreach (bool uploading in new[] { false, true })
        {
            using var f = new Fixture { IsCurrent = current, IsExpired = expired, FinishInProgress = finishing, FinalRemaining = final ? TimeSpan.Zero : TimeSpan.FromMinutes(2) };
            ClientSynchronizationCoordinator.Lease? lease = null;
            if (uploading) Check(f.Reload.Sync.TryAcquireSnapshot(out lease), "Fixture snapshot admission.");
            await f.Lifecycle.PollAsync(f);
            Check(f.Starts == (current && !final && expired && !finishing && !uploading ? 1 : 0)
                && f.Expiries == (current && final ? 1 : 0), "Lifecycle chose the wrong normal/final deadline or upload admission.");
            await f.Lifecycle.PollAsync(f);
            Check(f.Starts + f.Expiries <= 1, "Repeated callback ignored collaborator's consumed battle state.");
            lease?.Dispose();
        }
        foreach (var kind in new[] { LocalMutationKind.RaidRestore, LocalMutationKind.SnapshotRestore })
        {
            using var f = new Fixture { FinalRemaining = TimeSpan.Zero }; f.Reload.Mutations.Begin(kind, "loading");
            await f.Lifecycle.PollAsync(f); Check(f.Starts == 0 && f.Expiries == 0, "Lifecycle bypassed active scene replacement: " + kind);
            f.Reload.Mutations.Reset(); await f.Lifecycle.PollAsync(f); Check(f.Expiries == 1, "Completed scene replacement still blocked final expiry.");
        }
        using (var f = new Fixture { Ready = false })
        {
            f.Recovery.Reconcile(f, RecoveryData());
            for (int i = 0; i < 3; i++) { f.Lifecycle.Advance(); await f.Lifecycle.PollAsync(f); }
            Check(f.BattleReads == 0 && f.Starts == 0 && f.Expiries == 0 && f.Notices.Count == 1, "Pending recovery allowed deadlines or repeated waiting effects.");
            f.Ready = true; f.Lifecycle.Advance(); f.Lifecycle.Advance(); await f.Lifecycle.PollAsync(f);
            Check(f.Restores == 1 && !f.Recovery.BlocksBattleLifecycle && f.Starts == 0 && f.Expiries == 0, "Readiness did not restore authoritative future deadlines once.");
        }
        using (var f = new Fixture { Ready = false })
        {
            f.Recovery.Reconcile(f, RecoveryData()); f.Reload.Mode = "load-orphan";
            var loading = f.Reload.Start(); await f.Reload.Ports.PumpUntil(f.Reload.LoadQueued.Task);
            f.Reload.Change("game"); f.Lifecycle.Advance(); await f.Reload.Ports.PumpUntil(loading);
            Check(!f.Reload.Sync.IsBusy && !f.Reload.Mutations.IsPending && f.Restores == 0 && f.Reload.Completed == 0,
                "Lifecycle did not retire an abandoned load or restored recovery into its replacement.");
        }
        EngineEntrypoints();
    }
    private sealed class Fixture : IClientRaidLifecycleBattle, IClientRaidRecoveredBattle, IClientRaidSettlementActions,
        IClientRaidRecoveryRuntime, IClientRaidRecoveryPresentation, IDisposable
    {
        internal readonly ClientSnapshotReloadTestPorts Reload = new();
        internal readonly ClientRaidRecovery Recovery;
        internal readonly ClientRaidLifecycle Lifecycle;
        internal readonly List<ClientRaidRecoveryNotice> Notices = new();
        internal int Starts, Expiries, BattleReads, Restores;
        internal Action? OnStart, OnExpire;
        internal bool NoBattle;
        public bool Ready { get; set; } = true;
        public bool IsCurrent { get; set; } = true;
        public bool IsExpired { get; set; } = true;
        public bool FinishInProgress { get; set; }
        public TimeSpan FinalRemaining { get; set; } = TimeSpan.FromMinutes(2);
        public ClientRaidSettlementIdentity Identity => new("raid", "alice", "colony", "42");
        public bool HasMap => true;
        internal Fixture()
        {
            Recovery = new(Reload.Session, Reload.Ports, new(Reload.Session, Reload.Mutations, Reload.Sync, Reload.Ports, Reload.View), this, this);
            Lifecycle = new(Reload.Ports, Reload.Module, Recovery, Reload.Sync, this);
        }
        public IClientRaidRecoveredBattle? ReadBattle() { BattleReads++; return NoBattle ? null : this; }
        public void RestoreDeadlines(ClientRaidServerDeadlines deadlines)
        { Restores++; IsExpired = deadlines.DeadlineUtcTicks <= DateTime.UtcNow.Ticks; FinalRemaining = TimeSpan.FromTicks(deadlines.FinalDeadlineUtcTicks - DateTime.UtcNow.Ticks); }
        public string Format(ClientRaidRecoveryNotice notice) => notice.State.ToString();
        public void Report(ClientRaidRecoveryNotice notice, string message) => Notices.Add(notice);
        // These tests verify lifecycle selection. Settlement effects remain covered
        // by the real transaction/receipt/cleanup tests, without duplicating them.
        public Task StartAsync(IClientRaidSettlementRuntime battle, string reason)
        { Check(reason == "Expired", "Lifecycle changed settlement reason."); Starts++; FinishInProgress = true; OnStart?.Invoke(); return Task.CompletedTask; }
        public void Expire(IClientRaidSettlementRuntime battle, bool closeLocalMap = true)
        { Check(closeLocalMap, "Lifecycle expiry stopped closing the battle map."); Expiries++; IsCurrent = false; OnExpire?.Invoke(); }
        public bool TryBegin() => throw new NotSupportedException();
        public void KeepFinish() => throw new NotSupportedException();
        public void ResetFinish() => throw new NotSupportedException();
        public IClientRaidSupportLossSource SupportLosses => throw new NotSupportedException();
        public void EnqueueSaving(Action action, Action discarded) => throw new NotSupportedException();
        public bool CloseAccepted(string reason) => throw new NotSupportedException();
        public bool CloseExpired(bool closeLocalMap) => throw new NotSupportedException();
        public void Dispose() => Reload.Dispose();
    }
    private static ActiveRaidBattleSession? activeBattle;
    private static void EngineEntrypoints()
    {
        var harmony = new Harmony("ClashOfRim.Tests.RaidLifecycle"); using var engine = new ClientGameComponentTestRuntime(harmony.Id);
        harmony.Patch(AccessTools.PropertyGetter(typeof(ClashOfRimGameComponent), nameof(ClashOfRimGameComponent.ActiveRaidBattleSession)),
            prefix: new HarmonyMethod(typeof(ClientRaidLifecycleTests), nameof(ActiveBattle)));
        try
        {
            using var f = new Fixture { Ready = false }; var game = New<Game>(); Current.Game = game; f.Reload.Ports.GameOwner = game;
            activeBattle = new() { EventId = "raid", AttackerUserId = "alice", AttackerColonyId = "colony", DeadlineUtcTicks = 1, FinalDeadlineUtcTicks = DateTime.UtcNow.AddMinutes(2).Ticks };
            var component = new ClashOfRimGameComponent(game);
            AccessTools.Field(typeof(ClashOfRimGameComponent), "activeRaidBattleSession").SetValue(component, activeBattle);
            f.OnStart = () => activeBattle!.FinishInProgress = true; f.OnExpire = () => activeBattle = null;
            var callbacks = new ClientRaidLifecycleCallbacks(f.Lifecycle); ClientRaidLifecycleCallbacks.Attach(callbacks);
            f.Recovery.Reconcile(f, RecoveryData());
            component.GameComponentOnGUI(); component.GameComponentUpdate(); component.GameComponentTick();
            Check(f.Starts == 0 && f.Expiries == 0 && f.Notices.Count == 1 && f.BattleReads == 0, "Engine entry bypassed recovery readiness.");
            f.NoBattle = f.Ready = true; f.Recovery.Reconcile(f, null);
            component.GameComponentUpdate(); component.GameComponentOnGUI(); component.GameComponentTick();
            Check(f.Starts == 1 && activeBattle!.FinishInProgress, "Component did not use original battle or repeated normal expiry.");
            Check(f.Reload.Sync.TryAcquireSnapshot(out var lease), "Fixture upload lease.");
            activeBattle!.FinalDeadlineUtcTicks = 1; component.GameComponentUpdate(); lease.Dispose();
            Check(f.Expiries == 1 && activeBattle is null, "Final deadline was blocked by upload/finishing state.");
            var replacement = new ActiveRaidBattleSession { EventId = "other", DeadlineUtcTicks = 1, FinalDeadlineUtcTicks = 1 };
            activeBattle = replacement; Current.Game = New<Game>(); f.Reload.Ports.GameOwner = Current.Game;
            AccessTools.Field(typeof(ClashOfRimGameComponent), "activeRaidBattleSession").SetValue(component, replacement);
            component.GameComponentUpdate(); component.GameComponentOnGUI(); component.GameComponentTick();
            Check(f.Expiries == 1, "Old component expired the new game's battle.");
            component = new ClashOfRimGameComponent(Current.Game);
            AccessTools.Field(typeof(ClashOfRimGameComponent), "activeRaidBattleSession").SetValue(component, replacement);
            ClientRaidLifecycleCallbacks.Attach(new(f.Lifecycle)); callbacks.PollAsync(Current.Game, replacement).GetAwaiter().GetResult();
            Check(f.Expiries == 1, "Detached raid adapter expired a battle.");
            component.GameComponentTick(); Check(f.Expiries == 2, "Replacement component/adapter could not expire its own battle.");
        }
        finally { activeBattle = null; harmony.UnpatchAll(harmony.Id); }
    }
    private static ModActiveRaidRecoveryDto RecoveryData()
    {
        var now = DateTimeOffset.UtcNow;
        return new() { EventId = "raid", ServerNowUtc = now.ToString("O"), StartedAtUtc = now.AddMinutes(-1).ToString("O"),
            DeadlineUtc = now.AddMinutes(1).ToString("O"), FinalDeadlineUtc = now.AddMinutes(3).ToString("O") };
    }
    private static bool ActiveBattle(ref ActiveRaidBattleSession? __result) { __result = activeBattle; return false; }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
