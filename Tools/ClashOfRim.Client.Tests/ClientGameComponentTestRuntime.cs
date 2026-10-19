using System;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.WorldObjects;
using AIRsLight.ClashOfRim.Raids;
using HarmonyLib;
using Verse;

// Component entry tests run real domain callbacks. Only unrelated engine queue,
// legacy Mod lookup, clock and orphan cleanup are replaced by this shared shell.
internal sealed class ClientGameComponentTestRuntime : IDisposable
{
    private static ClientGameComponentTestRuntime? active;
    private readonly ClientGameComponentTestRuntime? previous = active;
    private readonly Game previousGame = Current.Game;
    private readonly ClientAutomaticSessionStartup? previousAutomatic = ClientAutomaticSessionStartup.Current;
    private readonly ClientColonySiteEvents? previousSites = ClientColonySiteEvents.Current;
    private readonly ClientWorldSynchronizationCallbacks? previousWorld = ClientWorldSynchronizationCallbacks.Current;
    private readonly ClientRaidLifecycleCallbacks? previousRaids = ClientRaidLifecycleCallbacks.Current;
    private readonly TickManager tickManager = (TickManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TickManager));
    private readonly Harmony harmony;
    internal int Drains { get; private set; }
    internal int Ticks;
    internal ClientGameComponentTestRuntime(string name)
    {
        active = this; harmony = new(name + ".GameComponent");
        ClientAutomaticSessionStartup.Attach(null); ClientColonySiteEvents.Attach(null);
        ClientWorldSynchronizationCallbacks.Attach(null);
        ClientRaidLifecycleCallbacks.Attach(null);
        void Prefix(System.Reflection.MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientGameComponentTestRuntime), prefix));
        Prefix(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.GetMod), Type.EmptyTypes, new[] { typeof(ClashOfRimMod) }), nameof(NoMod));
        Prefix(AccessTools.PropertyGetter(typeof(Find), nameof(Find.TickManager)), nameof(GetTicks));
        Prefix(AccessTools.PropertyGetter(typeof(TickManager), nameof(TickManager.TicksGame)), nameof(TickCount));
        Prefix(AccessTools.Method(typeof(ClashOfRimGameComponent), "RunMainThreadActions"), nameof(Drain));
        Prefix(AccessTools.Method(typeof(ClashOfRimGameComponent), "TryCleanupRemoteWorldObjectOrphans"), nameof(Skip));
    }
    public void Dispose()
    {
        Current.Game = previousGame; harmony.UnpatchAll(harmony.Id); active = previous;
        ClientAutomaticSessionStartup.Attach(previousAutomatic); ClientColonySiteEvents.Attach(previousSites);
        ClientWorldSynchronizationCallbacks.Attach(previousWorld);
        ClientRaidLifecycleCallbacks.Attach(previousRaids);
    }
    private static bool NoMod(ref ClashOfRimMod? __result) { __result = null; return false; }
    private static bool GetTicks(ref TickManager __result) { __result = active!.tickManager; return false; }
    private static bool TickCount(ref int __result) { __result = active!.Ticks; return false; }
    private static bool Drain() { active!.Drains++; return false; }
    private static bool Skip() => false;
}
