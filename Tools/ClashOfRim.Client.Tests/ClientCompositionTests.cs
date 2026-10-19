using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.WorldObjects;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using Verse;

internal static class ClientCompositionTests
{
    private static string? package;
    private static readonly List<(MethodBase Method, object[] Arguments)> publications = new();
    private static readonly List<string> warnings = new();
    private static readonly List<ClientSnapshotTransactionNotifications> observers = new();
    private static readonly List<(object Instance, object[] Arguments)> constructions = new();

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.ClientComposition");
        var culture = CultureInfo.CurrentCulture;
        var providers = typeof(ClashOfRimModNetworkClient).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType)).ToDictionary(f => f, f => f.GetValue(null));
        try
        {
            // Exercise the real composition root, replacing only engine persistence
            // and global installation. Do not patch generic Mod.GetSettings<T>.
            harmony.Patch(AccessTools.Constructor(typeof(Mod), new[] { typeof(ModContentPack) }),
                postfix: new HarmonyMethod(typeof(ClientCompositionTests), nameof(Settings)));
            harmony.Patch(AccessTools.GetDeclaredConstructors(typeof(ClientSnapshotTransactionNotifications)).Single(),
                postfix: new HarmonyMethod(typeof(ClientCompositionTests), nameof(ObserveSubscription)));
            foreach (Type type in new[] { typeof(ClientSnapshotMutationAdmission), typeof(ClientGiftOperations), typeof(ClientGiftActions), typeof(ClientTransporterActions) })
                harmony.Patch(AccessTools.GetDeclaredConstructors(type).Single(),
                    postfix: new HarmonyMethod(typeof(ClientCompositionTests), nameof(ObserveConstruction)));
            Patch(AccessTools.PropertyGetter(typeof(ModContentPack), nameof(ModContentPack.PackageId)), nameof(Package));
            Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }), nameof(Warning));
            Patch(AccessTools.Method(typeof(Log), nameof(Log.Message), new[] { typeof(string) }), nameof(Quiet));
            foreach (var method in typeof(ClashOfRimMod).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(m => m.Name.StartsWith("Attach", StringComparison.Ordinal) && m.ReturnType == typeof(void)))
                Patch(method, nameof(Publish));
            Patch(AccessTools.Method(typeof(ClashOfRimCompatibilityApi), nameof(ClashOfRimCompatibilityApi.BeginRegistrationCycle), new[] { typeof(string) }), nameof(Publish));
            Patch(AccessTools.Method(typeof(BuiltInClientCompatibility), nameof(BuiltInClientCompatibility.Apply)), nameof(Publish));
            Patch(AccessTools.Method(typeof(Harmony), nameof(Harmony.PatchAll), Type.EmptyTypes), nameof(InstallPatches));

            var errors = new List<string>();
            foreach (string name in new[] { "en-US", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                package = "airslight.clashofrim";
                publications.Clear(); warnings.Clear(); constructions.Clear();
                var mod = Create();
                CheckGiftComposition(mod, errors);
                ClientWorldMarkers? markers = null;
                foreach (Type adapter in new[] { typeof(SupportCaravanGizmoPatches), typeof(RaidCaravanGizmoPatches) })
                {
                    var call = publications.Single(p => p.Method.DeclaringType == adapter && p.Method.Name == "Attach");
                    if (call.Arguments[2] is not ClientWorldMarkers owner)
                        errors.Add(adapter.Name + " received an uninitialized marker owner.");
                    else if (markers is null) markers = owner;
                    else if (!ReferenceEquals(markers, owner)) errors.Add("Caravan adapters received different marker owners.");
                }
                if (publications.Count == 0 || warnings.Count != 0) errors.Add("Valid package did not install cleanly.");
                if (publications.Any(p => p.Arguments.Any(a => a is null))) errors.Add("Composition published a null dependency.");
                if (publications.GroupBy(p => p.Method).Any(g => g.Count() != 1)) errors.Add("Composition installed a binding more than once.");
                var acceptedProviders = providers.Keys.ToDictionary(f => f, f => f.GetValue(null));
                foreach (string? wrong in new[] { "unrelated.mod", "", null })
                {
                    package = wrong; publications.Clear(); warnings.Clear(); constructions.Clear();
                    _ = Create();
                    if (publications.Count != 0) errors.Add("Rejected package published " + publications.Count + " global bindings.");
                    if (warnings.Count != 1 || !warnings[0].Contains("unexpected package")) errors.Add("Rejected package lost its diagnostic.");
                    if (acceptedProviders.Any(p => !ReferenceEquals(p.Key.GetValue(null), p.Value))) errors.Add("Rejected package replaced accepted transport providers.");
                }
            }
            if (errors.Count != 0) throw new InvalidOperationException(string.Join(" | ", errors.Distinct()));
            Console.WriteLine("PASS: composition publishes complete owners only for the expected package (two cultures).");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            foreach (var entry in providers) entry.Key.SetValue(null, entry.Value);
            foreach (var observer in observers) ClientSnapshotComposition.Synchronization.SnapshotChanged -= observer.Observe;
            CultureInfo.CurrentCulture = culture;
            package = null; publications.Clear(); warnings.Clear(); observers.Clear(); constructions.Clear();
        }
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientCompositionTests), prefix));
    }

    private static ClashOfRimMod Create() => new((ModContentPack)RuntimeHelpers.GetUninitializedObject(typeof(ModContentPack)));
    private static void Settings(Mod __instance)
    {
        if (__instance is ClashOfRimMod)
            AccessTools.Field(typeof(Mod), "modSettings").SetValue(__instance, new ClashOfRimSettings());
    }
    private static bool Package(ref string __result) { __result = package!; return false; }
    private static void ObserveSubscription(ClientSnapshotTransactionNotifications __instance) => observers.Add(__instance);
    private static void ObserveConstruction(object __instance, object[] __args) => constructions.Add((__instance, __args));
    private static void CheckGiftComposition(ClashOfRimMod mod, List<string> errors)
    {
        var cleanup = publications.Single(p => p.Method.DeclaringType == typeof(ClientSessionCleanup));
        var synchronization = cleanup.Arguments.OfType<ClientSynchronizationCoordinator>().Single();
        var mutations = cleanup.Arguments.OfType<ClientLocalMutationCoordinator>().Single();
        foreach (var item in constructions)
        {
            if (item.Arguments.OfType<ClientSynchronizationCoordinator>().Any(value => !ReferenceEquals(value, synchronization))
                || item.Arguments.OfType<ClientLocalMutationCoordinator>().Any(value => !ReferenceEquals(value, mutations)))
                errors.Add("Gift arrival does not share the session synchronization and mutation owners.");
        }
        var admission = constructions.Single(c => c.Instance is ClientSnapshotMutationAdmission);
        var operations = constructions.Single(c => c.Instance is ClientGiftOperations);
        var actions = constructions.Single(c => c.Instance is ClientGiftActions);
        var transporter = constructions.Single(c => c.Instance is ClientTransporterActions);
        if (!ReferenceEquals(synchronization, ClientSnapshotComposition.Synchronization)
            || !ReferenceEquals(operations.Arguments[0], admission.Instance)
            || !ReferenceEquals(actions.Arguments[0], operations.Instance)
            || !ReferenceEquals(actions.Instance, mod.GiftActions)
            || !ReferenceEquals(transporter.Arguments[0], actions.Instance)
            || !ReferenceEquals(publications.Single(p => p.Method.DeclaringType == typeof(ClientTransporterActions)).Arguments[0], transporter.Instance))
            errors.Add("Engine gift binding bypasses the composed gift admission or operations.");
    }
    private static bool Publish(MethodBase __originalMethod, object[] __args)
    { publications.Add((__originalMethod, __args)); return false; }
    private static bool InstallPatches(Harmony __instance)
    {
        if (__instance.Id != ClashOfRimMod.PackageId) return true;
        publications.Add((AccessTools.Method(typeof(Harmony), nameof(Harmony.PatchAll), Type.EmptyTypes), Array.Empty<object>()));
        return false;
    }
    private static bool Warning(string __0) { warnings.Add(__0); return false; }
    private static bool Quiet() => false;
}
