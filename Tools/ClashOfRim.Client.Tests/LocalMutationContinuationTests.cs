using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using HarmonyLib;
using Verse;

// Temporary integration coverage for callers that remain in the Mod until M4/M5.
// Only game serialization and localization are replaced; admission, failure state,
// retry dispatch and synchronization ownership use the production implementation.
internal static class LocalMutationContinuationTests
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int saves;

    public static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.LocalMutationContinuation");
        harmony.Patch(typeof(ClashOfRimText).GetMethod("Key", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(NamedArgument[]) }, null), prefix: new HarmonyMethod(typeof(LocalMutationContinuationTests), nameof(Translate)));
        harmony.Patch(AccessTools.Method(typeof(ClashOfRimGameComponent), "TrySaveCurrentGameToBytes",
            new[] { typeof(byte[]).MakeByRefType(), typeof(string).MakeByRefType() }),
            prefix: new HarmonyMethod(typeof(LocalMutationContinuationTests), nameof(FailSave)));
        ClientSessionWork.Reset();
        try
        {
            Verify("StartBuildAndSubmitPreparedBankLoanTransaction", LocalMutationKind.Bank,
                "loan-key", 10L, 100, new ModBankLoanSummaryDto(), new ModBankStatusResponseDto());
            Verify("StartBuildAndSubmitPreparedBankLoanRepayment", LocalMutationKind.Bank,
                "repay-key", 10L, new ModBankLoanSummaryDto(), new ModBankStatusResponseDto());
            Verify("StartBuildAndSubmitPreparedBankDebtRepayment", LocalMutationKind.Bank,
                "debt-key", 10L, new ModBankDebtSummaryDto(), new ModBankStatusResponseDto());
            var singleType = typeof(ClashOfRimMod).GetNestedType("EventApplicationSnapshotConfirmationRequest", BindingFlags.NonPublic)!;
            var single = Activator.CreateInstance(singleType, nonPublic: true)!;
            singleType.GetProperty("EventId")!.SetValue(single, "event");
            singleType.GetProperty("Operation")!.SetValue(single, LocalMutationKind.Gift);
            Verify("StartConfirmEventApplicationSnapshot", LocalMutationKind.Gift, single);
            var batchType = typeof(ClashOfRimMod).GetNestedType("EventApplicationsSnapshotConfirmationRequest", BindingFlags.NonPublic)!;
            var batch = Activator.CreateInstance(batchType, nonPublic: true)!;
            batchType.GetProperty("Applications")!.SetValue(batch, new[] { new ModConfirmEventApplicationEntryDto { EventId = "event" } });
            batchType.GetProperty("Operation")!.SetValue(batch, LocalMutationKind.AutomaticEvents);
            Verify("StartConfirmEventApplicationsSnapshot", LocalMutationKind.AutomaticEvents, batch);
        }
        finally
        {
            ClientSessionWork.Reset();
            typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.MainMenu.ClashOfRimMainMenuPatches", true)!
                .GetMethod("RunQueuedMainThreadActions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private static void Verify(string method, LocalMutationKind kind, params object[] args)
    {
        var mod = (ClashOfRimMod)RuntimeHelpers.GetUninitializedObject(typeof(ClashOfRimMod));
        var mutations = new ClientLocalMutationCoordinator();
        var synchronization = new ClientSynchronizationCoordinator();
        typeof(ClashOfRimMod).GetField("localMutations", Instance)!.SetValue(mod, mutations);
        typeof(ClashOfRimMod).GetField("synchronization", Instance)!.SetValue(mod, synchronization);
        typeof(ClashOfRimMod).GetField("settings", Instance)!.SetValue(mod, new ClashOfRimSettings
        { ServerBaseUrl = "http://localhost", UserId = "alice", ColonyId = "colony", CurrentSnapshotId = "snapshot" });
        saves = 0;
        var failure = mutations.RecordFailure(kind, "previous failure", () =>
        {
            try { typeof(ClashOfRimMod).GetMethod(method, Instance)!.Invoke(mod, args); }
            catch (TargetInvocationException ex) { throw new Exception(method + ": " + ex.InnerException); }
        }, true);
        if (!mutations.TryRetry(failure) || saves != 1 || synchronization.IsBusy
            || !mutations.IsPending || mutations.Current!.Kind != kind || mutations.Current.Status == "previous failure"
            || mutations.IsCurrent(failure))
            throw new Exception(method + " blocked its own confirmation, lost pending state, or leaked synchronization admission.");
    }

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
