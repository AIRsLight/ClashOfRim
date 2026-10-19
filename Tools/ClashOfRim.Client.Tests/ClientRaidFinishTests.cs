using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class ClientRaidFinishTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        foreach (string change in new[] { "user", "auth", "server", "colony", "game", "login", "generation", "unavailable", "battle", "finishing" })
        {
            using var f = new ClientRaidSettlementTests.Fixture();
            var command = f.Module.PrepareFinish(f, "ManualFinish")!;
            Check(command.IsCurrent && !f.Sync.IsBusy && !f.Finishing, "Preparing confirmation consumed settlement admission.");
            switch (change)
            {
                case "unavailable": f.Session.BeginUnavailable(f.Ports.Auth, null); break;
                case "battle": f.Current = false; break;
                case "finishing": f.Finishing = true; break;
                default: f.Change(change); break;
            }
            await f.Pump(command.StartAsync());
            Check(!command.IsCurrent && f.SourceCaptures == 0 && f.Commands.Count == 0 && !f.Sync.IsBusy, "Old finish confirmation crossed " + change);
        }
        using (var f = new ClientRaidSettlementTests.Fixture())
        {
            var command = f.Module.PrepareFinish(f, "手动-é")!; f.Change("snapshot");
            Task? reentrant = null; f.OnSupport = () => reentrant = command.StartAsync();
            Task work = command.StartAsync(); await command.StartAsync(); await f.Pump(work); await reentrant!;
            Check(f.Completed == 1 && f.SourceCaptures == 1 && f.Commands.Single().IdempotencyKey.EndsWith(":手动-é", StringComparison.Ordinal),
                "Finish command lost its reason, latest snapshot, or once-only settlement.");
        }
        using (var f = new ClientRaidSettlementTests.Fixture())
        {
            var command = f.Module.PrepareFinish(f, "ManualFinish")!;
            Check(f.Sync.TryBeginManual(out var lease), "Fixture admission."); await f.Pump(command.StartAsync()); lease!.Dispose();
            await f.Pump(command.StartAsync()); Check(f.SourceCaptures == 0 && !f.Finishing, "Rejected finish callback later executed again.");
            await f.Pump(f.Module.PrepareFinish(f, "ManualFinish")!.StartAsync()); Check(f.Completed == 1, "Busy rejection prevented a new explicit finish choice.");
        }
        using (var f = new ClientRaidSettlementTests.Fixture { Finishing = true })
            Check(f.Module.PrepareFinish(f, "ManualFinish") is null, "Already finishing battle offered another command.");
        await EngineActions();
    }
    private static ClientRaidSettlementTests.Fixture fixture = null!;
    private static ActiveRaidBattleSession? battle;
    private static ClashOfRimGameComponent component = null!;
    private static RemoteSessionMapParent parent = null!;
    private static Map map = null!;
    private static Action? confirm, translating;
    private static int windows, pawnCount;
    private static ClientOverlayTestRuntime gui = null!;
    private static Action? clicking;
    private static readonly List<Task> dispatched = new();
    private static async Task EngineActions()
    {
        var harmony = new Harmony("ClashOfRim.Tests.RaidFinish"); using var engine = new ClientGameComponentTestRuntime(harmony.Id);
        using var drawing = new ClientOverlayTestRuntime(harmony.Id); gui = drawing;
        void Prefix(System.Reflection.MethodBase method, string name) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientRaidFinishTests), name));
        void Method(Type type, string method, string name) => Prefix(AccessTools.Method(type, method), name);
        void Getter(Type type, string property, string name) => Prefix(AccessTools.PropertyGetter(type, property), name);
        Getter(typeof(Find), nameof(Find.WindowStack), nameof(Stack));
        Method(typeof(WindowStack), nameof(WindowStack.Add), nameof(Add));
        Method(typeof(ClientRaidFinishActions), "CountPlayerPawns", nameof(Pawns));
        Getter(typeof(ClientRaidSettlementRuntime), nameof(ClientRaidSettlementRuntime.SupportLosses), nameof(Support));
        Method(typeof(ClientRaidSettlementRuntime), nameof(ClientRaidSettlementRuntime.EnqueueSaving), nameof(Save));
        Method(typeof(ClientRaidSettlementRuntime), nameof(ClientRaidSettlementRuntime.CloseAccepted), nameof(Close));
        Method(typeof(ClientRemoteSessionRuntime), nameof(ClientRemoteSessionRuntime.TryBeginRaidBattleFinish), nameof(Begin));
        Method(typeof(ClientRemoteSessionRuntime), nameof(ClientRemoteSessionRuntime.KeepRaidBattleFinishInProgress), nameof(Keep));
        Method(typeof(ClientRemoteSessionRuntime), nameof(ClientRemoteSessionRuntime.ResetRaidBattleFinish), nameof(Reset));
        Getter(typeof(Map), nameof(Map.Parent), nameof(Parent));
        Method(typeof(ClashLog), nameof(ClashLog.Message), nameof(Skip));
        Method(typeof(Log), nameof(Log.Warning), nameof(Skip));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Dialog_MessageBox)).Where(m => m.Name == nameof(Dialog_MessageBox.CreateConfirmation))) Prefix(method, nameof(Confirm));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Messages)).Where(m => m.Name == nameof(Messages.Message))) Prefix(method, nameof(Skip));
        harmony.Patch(AccessTools.Method(typeof(ClientRaidSettlements.FinishCommand), nameof(ClientRaidSettlements.FinishCommand.StartAsync)),
            postfix: new HarmonyMethod(typeof(ClientRaidFinishTests), nameof(Dispatch)));
        try
        {
            foreach (int pawns in new[] { 0, 3 })
            {
                using var f = SetUp(); pawnCount = pawns;
                Check(RemoteMapSessionController.TryRequestRaidBattleFinish(battle, "ManualFinish", true) && windows == 1 && confirm is not null,
                    "Facade failed to show the typed finish confirmation.");
                Check(gui.TextKeys.Single().Contains(pawns == 0 ? "ConfirmFinishNoPawns" : "ConfirmFinishWithPlayerPawns") && !f.Sync.IsBusy && !f.Finishing,
                    "Confirmation changed pawn branch or started before confirmation.");
                confirm!(); confirm(); await Pump();
                Check(f.Completed == 1 && f.SourceCaptures == 1 && f.Commands.Count == 1, "Native confirmation bypassed/duplicated original settlement pipeline.");
            }
            foreach (string change in new[] { "auth", "login", "game", "event", "target", "battle", "adapter" })
            {
                using var f = SetUp(); var old = battle;
                Check(RemoteMapSessionController.TryRequestRaidBattleFinish(battle, "ManualFinish", true), "Fixture confirmation.");
                switch (change)
                {
                    case "game": Current.Game = New<Game>(); f.Ports.GameOwner = Current.Game; break;
                    case "event": battle!.EventId = "changed"; break;
                    case "target": battle!.TargetMapId = "changed"; break;
                    case "battle": battle = new() { EventId = "raid", AttackerUserId = "alice", AttackerColonyId = "colony", TargetMapId = "42" }; ClashOfRimGameComponent.SetActiveRaidBattleSession(battle); break;
                    case "adapter": ClientRaidFinishActions.Attach(new(f.Module)); break;
                    default: f.Change(change); break;
                }
                confirm!(); await Pump();
                Check(f.SourceCaptures == 0 && f.Commands.Count == 0 && !old!.FinishInProgress, "Native old confirmation crossed " + change);
            }
            using (var f = SetUp())
            {
                translating = () => f.Change("auth");
                Check(!RemoteMapSessionController.TryRequestRaidBattleFinish(battle, "ManualFinish", true) && windows == 0,
                    "Reason translation displayed an obsolete confirmation.");
            }
            using (var f = SetUp())
            {
                var click = ClientRaidFinishActions.Current!.Prepare(battle, "ManualFinish", true)!;
                Check(click() && !click() && windows == 1, "One prepared button displayed multiple confirmation windows.");
            }
            using (var f = SetUp())
            {
                Check(ClashOfRimGameComponent.TryHandleRaidBattleDirectMapRemoval(map), "Direct map removal did not retain its settlement interception.");
                Check(ClashOfRimGameComponent.TryHandleRaidBattleDirectMapRemoval(map), "Repeated direct map removal escaped while finishing.");
                await Pump();
                Check(f.Completed == 1 && windows == 0 && f.Commands.Single().IdempotencyKey.EndsWith(":DirectMapRemoval", StringComparison.Ordinal),
                    "Map removal failed to use the same settlement command and reason.");
            }
            using (var f = SetUp())
            {
                ClientRaidFinishActions.Attach(null);
                Check(ClashOfRimGameComponent.TryHandleRaidBattleDirectMapRemoval(map) && f.SourceCaptures == 0,
                    "Unbound finish commands allowed destructive vanilla removal.");
            }
            foreach (string condition in new[] { "active", "expired", "finishing", "world", "entry", "old-game", "unbound", "draw-auth" })
            {
                using var f = SetUp();
                if (condition == "expired") battle!.DeadlineUtcTicks = 1;
                if (condition == "finishing") battle!.FinishInProgress = true;
                if (condition == "world") gui.WorldSelected = true;
                if (condition == "entry") Current.ProgramState = ProgramState.Entry;
                if (condition == "old-game") Current.Game = New<Game>();
                if (condition == "unbound") ClientRaidFinishActions.Attach(null);
                if (condition == "draw-auth") clicking = () => f.Change("auth");
                component.GameComponentOnGUI();
                bool hidden = condition is "world" or "entry" or "old-game";
                Check(gui.Buttons.Count == (hidden ? 0 : 1), "Overlay visibility changed: " + condition);
                Check(windows == (condition == "active" ? 1 : 0) && f.SourceCaptures == 0, "Overlay button crossed eligibility or started before confirmation: " + condition);
                if (condition is "expired" or "finishing")
                    Check(gui.Labels.Contains("ClashOfRim.Raid.StatusSettlementUploading"), "Empty settlement status lost fallback.");
                if (condition == "active") { confirm!(); await Pump(); Check(f.Completed == 1, "Overlay button failed to invoke original settlement."); }
            }
        }
        finally
        {
            battle = null; confirm = translating = clicking = null; dispatched.Clear(); harmony.UnpatchAll(harmony.Id);
        }
    }
    private static ClientRaidSettlementTests.Fixture SetUp()
    {
        fixture = new(); var game = New<Game>(); Current.Game = game; fixture.Ports.GameOwner = game;
        battle = new() { EventId = "raid", AttackerUserId = "alice", AttackerColonyId = "colony", TargetMapId = "42", ClientMapId = "Map_42",
            DeadlineUtcTicks = DateTime.UtcNow.AddMinutes(1).Ticks, FinalDeadlineUtcTicks = DateTime.UtcNow.AddMinutes(3).Ticks };
        component = new(game); AccessTools.Field(typeof(Game), "components").SetValue(game, new List<GameComponent> { component });
        ClashOfRimGameComponent.SetActiveRaidBattleSession(battle);
        parent = New<RemoteSessionMapParent>(); parent.Mode = "RaidBattle"; map = New<Map>(); map.uniqueID = 42;
        ClientRaidFinishActions.Attach(new(fixture.Module)); confirm = translating = null; windows = pawnCount = 0; dispatched.Clear();
        ClientRaidBattleOverlay.Attach(new(ClientRaidFinishActions.Current!, () => null));
        gui.Reset(); clicking = null;
        gui.Click = _ => { clicking?.Invoke(); return true; };
        gui.Translating = _ => { var hook = translating; translating = null; hook?.Invoke(); };
        return fixture;
    }
    private static async Task Pump() { var tasks = Task.WhenAll(dispatched.ToArray()); dispatched.Clear(); await fixture.Pump(tasks); }
    private static void Dispatch(Task __result) => dispatched.Add(__result);
    private static bool Parent(ref MapParent __result) { __result = parent; return false; }
    private static bool Stack(ref WindowStack __result) { __result = New<WindowStack>(); return false; }
    private static bool Add() { windows++; return false; }
    private static bool Pawns(ref int __result) { __result = pawnCount; return false; }
    private static bool Confirm(object[] __args, ref Dialog_MessageBox __result) { confirm = (Action)__args[1]; __result = New<Dialog_MessageBox>(); return false; }
    private static bool Support(ref IClientRaidSupportLossSource __result) { __result = fixture; return false; }
    private static bool Save(Action action, Action discarded) { fixture.EnqueueSaving(action, discarded); return false; }
    private static bool Begin(ref bool __result)
    { __result = battle is not null && !battle.FinishInProgress; if (__result) { battle!.FinishInProgress = true; fixture.Finishing = true; } return false; }
    private static bool Keep() { battle!.FinishInProgress = true; fixture.Finishing = true; return false; }
    private static bool Reset() { if (battle is not null) battle.FinishInProgress = false; fixture.Finishing = false; return false; }
    private static bool Close(string reason, ref bool __result) { __result = fixture.CloseAccepted(reason); battle = null; return false; }
    private static bool Skip() => false;
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
