using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;
using HarmonyLib;
using Verse;

// Temporary integration coverage for callers that remain in the Mod until M4/M5.
// Only game serialization and localization are replaced; admission, failure state,
// retry dispatch and synchronization ownership use the production implementation.
internal static class LocalMutationContinuationTests
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int saves;
    private static int presentedFailures;

    public static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.LocalMutationContinuation");
        harmony.Patch(typeof(ClashOfRimText).GetMethod("Key", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(NamedArgument[]) }, null), prefix: new HarmonyMethod(typeof(LocalMutationContinuationTests), nameof(Translate)));
        harmony.Patch(AccessTools.Method(typeof(ClashOfRimGameComponent), "TrySaveCurrentGameToBytes",
            new[] { typeof(bool), typeof(byte[]).MakeByRefType(), typeof(string).MakeByRefType() }),
            prefix: new HarmonyMethod(typeof(LocalMutationContinuationTests), nameof(FailSave)));
        harmony.Patch(AccessTools.Method(typeof(ClientSnapshotFailureView), nameof(ClientSnapshotFailureView.Show)),
            prefix: new HarmonyMethod(typeof(LocalMutationContinuationTests), nameof(ObserveFailureView)));
        var originalGame = Current.Game;
        var originalState = Current.ProgramState;
        Current.Game = null;
        Current.ProgramState = ProgramState.Entry;
        ClientSessionWork.Reset();
        try
        {
            Verify("StartBuildAndSubmitPreparedBankLoanTransaction", LocalMutationKind.Bank,
                "loan-key", 10L, 100, new ModBankLoanSummaryDto(), new ModBankStatusResponseDto());
            Verify("StartBuildAndSubmitPreparedBankLoanRepayment", LocalMutationKind.Bank,
                "repay-key", 10L, new ModBankLoanSummaryDto(), new ModBankStatusResponseDto());
            Verify("StartBuildAndSubmitPreparedBankDebtRepayment", LocalMutationKind.Bank,
                "debt-key", 10L, new ModBankDebtSummaryDto(), new ModBankStatusResponseDto());
            Verify("StartConfirmGiftApplication", LocalMutationKind.Gift, "event", "ItemDeliveryAnchored");
            Verify("StartConfirmAutomaticEventApplications", LocalMutationKind.AutomaticEvents,
                (object)new[] { new ModConfirmEventApplicationEntryDto { EventId = "event" } });
        }
        finally
        {
            ClientSessionWork.Reset();
            typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.MainMenu.ClashOfRimMainMenuPatches", true)!
                .GetMethod("RunQueuedMainThreadActions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            harmony.UnpatchAll(harmony.Id);
            Current.Game = originalGame;
            Current.ProgramState = originalState;
        }
    }

    private static void Verify(string method, LocalMutationKind kind, params object[] args)
    {
        var mod = (ClashOfRimMod)RuntimeHelpers.GetUninitializedObject(typeof(ClashOfRimMod));
        var mutations = new ClientLocalMutationCoordinator();
        var synchronization = new ClientSynchronizationCoordinator();
        typeof(ClashOfRimMod).GetField("localMutations", Instance)!.SetValue(mod, mutations);
        typeof(ClashOfRimMod).GetField("synchronization", Instance)!.SetValue(mod, synchronization);
        typeof(ClashOfRimMod).GetField("snapshotPreparation", Instance)!.SetValue(mod, ClientSnapshotComposition.CreatePreparation());
        var settings = new ClashOfRimSettings
        { ServerBaseUrl = "http://localhost", UserId = "alice", ColonyId = "colony", CurrentSnapshotId = "snapshot" };
        typeof(ClashOfRimMod).GetField("settings", Instance)!.SetValue(mod, settings);
        var lifecycle = new ClientSessionLifecycle(new ClientSessionIdentityStore(settings), new ClientSessionLogoutTransport(),
            new ClientSessionConnection(new ClientSessionWebSocketTransport(new ClientWebSocketFactory(), new ClientProtocolMetadata())));
        var game = new ClientSnapshotGame();
        var inbox = new ClientEventInbox();
        var confirmation = new ClientEventConfirmation(lifecycle, game, ClientSnapshotComposition.CreatePublication(settings),
            inbox, new ClientEventConfirmationTransport());
        var preparation = new ClientEventConfirmationPreparation(lifecycle, synchronization, mutations, confirmation);
        var recovery = new ClientSnapshotFailureRecovery(lifecycle, mutations, synchronization, game, new ClientSnapshotFailureView());
        typeof(ClashOfRimMod).GetField("snapshotFailureRecovery", Instance)!.SetValue(mod, recovery);
        typeof(ClashOfRimMod).GetField("eventConfirmations", Instance)!.SetValue(mod,
            new ClientEventConfirmationWorkflow(lifecycle, preparation, confirmation, mutations, recovery, inbox,
                new AIRsLight.ClashOfRim.EventLetters.ClientEventConfirmationPresentation(static (_, _) => { }),
                new AIRsLight.ClashOfRim.EventLetters.ClientEventConfirmationEffects()));
        saves = 0;
        presentedFailures = 0;
        var failure = mutations.RecordFailure(kind, "previous failure", () =>
        {
            try { typeof(ClashOfRimMod).GetMethod(method, Instance)!.Invoke(mod, args); }
            catch (TargetInvocationException ex) { throw new Exception(method + ": " + ex.InnerException); }
        }, true);
        if (!mutations.TryRetry(failure) || saves != 1 || synchronization.IsBusy
            || !mutations.IsPending || mutations.Current!.Kind != kind || mutations.Current.Status == "previous failure"
            || mutations.IsCurrent(failure))
            throw new Exception(method + " blocked its own confirmation, lost pending state, or leaked synchronization admission.");
        typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.MainMenu.ClashOfRimMainMenuPatches", true)!
            .GetMethod("RunQueuedMainThreadActions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        if (presentedFailures != 1) throw new Exception(method + " failure did not reach the main-menu view.");
    }

    private static bool ObserveFailureView() { presentedFailures++; return false; }
    private static bool Translate(string key, ref string __result) { __result = key; return false; }
    private static bool FailSave(ref byte[] saveBytes, ref string failureReason, ref bool __result)
    {
        saves++;
        saveBytes = Array.Empty<byte>();
        failureReason = "fixture serialization failure";
        __result = false;
        return false;
    }
}
