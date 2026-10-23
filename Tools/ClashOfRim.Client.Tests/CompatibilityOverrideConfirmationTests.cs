using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using HarmonyLib;
using Verse;

internal static class CompatibilityOverrideConfirmationTests
{
    private static Action? confirm;
    private static string message = "";
    private static Task? operation;
    private static int opened;

    internal static async Task RunAsync()
    {
        using var gui = new ClientOverlayTestRuntime("ClashOfRim.Tests.OverrideConfirmation");
        var harmony = new Harmony("ClashOfRim.Tests.OverrideConfirmation");
        void Patch(System.Reflection.MethodBase method, string prefix)
            => harmony.Patch(method, prefix: new HarmonyMethod(typeof(CompatibilityOverrideConfirmationTests), prefix));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WindowStack)), nameof(Stack));
        Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), nameof(Add));
        Patch(AccessTools.Method(typeof(Window), nameof(Window.PreOpen)), nameof(Skip));
        Patch(AccessTools.Method(typeof(ServerEntryProgressWindow), nameof(ServerEntryProgressWindow.CloseCurrent)), nameof(Skip));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Dialog_MessageBox)).Where(m => m.Name == nameof(Dialog_MessageBox.CreateConfirmation)))
            Patch(method, nameof(Confirmation));
        try
        {
            foreach (string order in new[] { "same", "different", "unknown" })
            foreach (string action in new[] { "cancel", "confirm", "stale" })
            {
                var f = new ClientCompatibilityOverrideTests.Fixture();
                bool available = true; int closed = 0;
                var intent = ClientCompatibilityOverrideIntent.Prepare(new Order(order), f.Flow, new[] { "mod.i" },
                    effect => { if (!available) return false; effect(); return true; }, () => available)!;
                confirm = null; operation = null; opened = 0; message = "";
                _ = CompatibilityBaselineOverrideWindow.CreateConfirmation(intent, () => closed++);
                Check(confirm is not null && message.Contains("Override.Confirm", StringComparison.Ordinal)
                    && message.Contains("ModListWarning", StringComparison.Ordinal) == (order != "same"), "Confirmation missing or mod-order detail lost.");
                Check(f.Calls.Count == 0 && !f.Access.Busy && opened == 0 && closed == 0, "Opening confirmation already submitted a baseline.");
                if (action == "cancel") continue; // Closing/canceling the native dialog does not invoke its accept action.
                if (action == "stale") available = false;
                confirm!(); confirm!();
                if (action == "stale")
                { Check(opened == 0 && closed == 0 && f.Calls.Count == 0, "Expired confirmation opened or submitted recovery."); continue; }
                Check(operation is not null && opened == 1 && closed == 1, "Confirmation opened duplicate progress windows.");
                await f.Ports.PumpUntil(operation!);
                Check(f.Calls.SequenceEqual(new[] { "override", "requirements", "submit" }) && !f.Access.Busy,
                    "Confirmed override duplicated or bypassed the shared workflow.");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); confirm = null; operation = null; ClientSessionWork.Reset(); }
    }
    private sealed record Order(string Mode) : IClientCompatibilityModOrderSource
    {
        public string SteamPostfix => "_steam";
        public IReadOnlyList<string>? ReadPackageOrder() => Mode == "unknown" ? null : new[] { Mode == "same" ? "mod.i" : "mod.other" };
    }
    private static bool Confirmation(object[] __args, ref Dialog_MessageBox __result)
    { message = __args[0].ToString()!; confirm = (Action)__args[1]; __result = New<Dialog_MessageBox>(); return false; }
    private static bool Stack(ref WindowStack __result) { __result = New<WindowStack>(); return false; }
    private static bool Add(Window __0)
    {
        opened++; __0.PreOpen();
        operation = (Task?)AccessTools.Field(typeof(CompatibilityBaselineOverrideWindow), "operation").GetValue(__0);
        return false;
    }
    private static bool Skip() => false;
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
