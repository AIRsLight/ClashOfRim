using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.WorldObjects;
using ClashOfRim.TestSupport;
using HarmonyLib;
using RimWorld.Planet;

internal static class TransportPodGiftFixtureTests
{
    private static int nativeEntries;

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.TransportPodGiftFixture");
        var previous = ClientTransporterActions.Current;
        var original = InertBinding();
        ClientSessionWork.Reset();
        try
        {
            var roots = new[] { typeof(ClientGiftTestFixture), typeof(TransportPodGiftContention) };
            Type Owner(Type type) => type.DeclaringType is { } parent ? Owner(parent) : type;
            var violations = ModuleDependencyGuard.FindViolations(
                typeof(TransportPodGiftFixtureTests).Assembly.GetTypes().Where(type => roots.Contains(Owner(type))),
                type => type == typeof(ClashOfRimMod) || type == typeof(ClashOfRimSettings) || type == typeof(IServiceProvider)
                    || type.Assembly == typeof(TransportPodGiftFixtureTests).Assembly && Owner(type).Name.EndsWith("Tests", StringComparison.Ordinal));
            Check(violations.Count == 0, string.Join(Environment.NewLine, violations));
            // Execute the real Harmony dispatch for native Arrived. Stop only the
            // engine body, which requires a running world, after the product guard.
            harmony.CreateClassProcessor(typeof(RemoteGiftArrivalGuard)).Patch();
            var arrived = AccessTools.Method(typeof(TravellingTransporters), "Arrived");
            harmony.Patch(arrived, prefix: new HarmonyMethod(typeof(TransportPodGiftFixtureTests), nameof(StopNative)) { priority = Priority.Last });
            var voyage = (TravellingTransporters)RuntimeHelpers.GetUninitializedObject(typeof(TravellingTransporters));
            voyage.arrivalAction = new RemoteGiftTransportersArrivalAction();
            void Arrive() => arrived.Invoke(voyage, null);

            foreach (string failure in new[] { "", "snapshot", "manual", "atomic mutation" })
            {
                ClientTransporterActions.Attach(original);
                var generation = ClientSessionWork.Token;
                var fixture = new ClientGiftTestFixture(ClientGiftSourceKind.TransportPods, resetSession: false);
                var seen = new List<string>();
                var expected = new InvalidOperationException("probe failure");
                int before = nativeEntries;
                try
                {
                    TransportPodGiftContention.Run(fixture, kind =>
                    {
                        seen.Add(kind);
                        Check(fixture.Sync.SnapshotInProgress == (kind == "snapshot")
                            && fixture.Sync.ManualInProgress == (kind != "atomic mutation")
                            && fixture.Mutations.IsPending == (kind == "atomic mutation"), "Busy modes were not independent.");
                        Arrive();
                        Check(nativeEntries == before, "Busy arrival passed the product Harmony guard: " + kind);
                        if (kind == failure) throw expected;
                    });
                    Check(failure == "", "Probe exception was swallowed.");
                }
                catch (InvalidOperationException ex) when (ReferenceEquals(ex, expected)) { }
                Check(seen.SequenceEqual(new[] { "snapshot", "manual", "atomic mutation" }.Take(failure == "snapshot" ? 1 : failure == "manual" ? 2 : 3)), "Probe order changed.");
                Check(ReferenceEquals(ClientTransporterActions.Current, original) && !fixture.Sync.IsBusy && !fixture.Mutations.IsPending
                    && ClientSessionWork.Token == generation && ClientSessionWork.IsCurrent, "Probe cleanup affected the live binding, generation or owned state.");
                Check(fixture.Captured == 0 && fixture.Submissions == 0, "Busy probe started a fake gift.");
            }

            foreach (bool replaceSession in new[] { false, true })
            foreach (string phase in new[] { "snapshot", "manual", "atomic mutation" })
            {
                ClientTransporterActions.Attach(original);
                var replacement = InertBinding();
                var fixture = new ClientGiftTestFixture(ClientGiftSourceKind.TransportPods, resetSession: false);
                bool rejected = false;
                try
                {
                    TransportPodGiftContention.Run(fixture, kind =>
                    {
                        if (kind != phase) return;
                        if (replaceSession) ClientSessionWork.Reset();
                        else ClientTransporterActions.Attach(replacement);
                    });
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("replaced its session")) { rejected = true; }
                Check(rejected && !fixture.Sync.IsBusy && !fixture.Mutations.IsPending
                    && ReferenceEquals(ClientTransporterActions.Current, replaceSession ? original : replacement), "Reentry retained work or overwrote a newer binding.");
            }

            // A non-gift action must reach the engine boundary. This detects a
            // harness that would skip every native invocation regardless of guard.
            voyage.arrivalAction = new RemoteRaidTransportersArrivalAction();
            int idle = nativeEntries;
            Arrive();
            Check(nativeEntries == idle + 1, "Native dispatch observation never exercised the product guard.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            ClientTransporterActions.Attach(previous);
            ClientSessionWork.Reset();
            nativeEntries = 0;
        }
    }

    private static ClientTransporterActions InertBinding() => (ClientTransporterActions)RuntimeHelpers.GetUninitializedObject(typeof(ClientTransporterActions));
    private static bool StopNative() { nativeEntries++; return false; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
