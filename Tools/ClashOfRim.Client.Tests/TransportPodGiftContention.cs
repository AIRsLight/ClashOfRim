using System;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.WorldObjects;

// Synchronous engine fixture. Busy states belong to the supplied domain fixture;
// the live binding is restored before the real gift is dispatched.
internal static class TransportPodGiftContention
{
    internal static void Run(ClientGiftTestFixture fixture, Action<string> arrive)
    {
        if (fixture.Sync.IsBusy || fixture.Mutations.IsPending)
            throw new InvalidOperationException("Contention fixture requires idle owned domains.");
        var previous = ClientTransporterActions.Current;
        var generation = ClientSessionWork.Token;
        var gifts = new ClientGiftActions(fixture.Gifts, fixture.Session, fixture.Ports, fixture,
            () => fixture.Capabilities, () => Array.Empty<ModWorldMapMarkerDto>());
        // No raid is dispatched by this gift-only fixture. Keep the unused dependency
        // inert instead of constructing another session or resetting the live generation.
        var raids = (ClientRaidCommands)RuntimeHelpers.GetUninitializedObject(typeof(ClientRaidCommands));
        var binding = new ClientTransporterActions(gifts, raids, () => fixture.Capabilities);
        ClientTransporterActions.Attach(binding);
        try
        {
            if (!fixture.Sync.TryAcquireSnapshot(out var snapshot)) throw new InvalidOperationException("Snapshot admission failed.");
            using (snapshot) Probe("snapshot");
            if (!fixture.Sync.TryBeginManual(out var manual)) throw new InvalidOperationException("Manual admission failed.");
            using (manual) Probe("manual");
            try { fixture.Mutations.Begin(LocalMutationKind.GiftSend, "fixture"); Probe("atomic mutation"); }
            finally { fixture.Mutations.Reset(); }
        }
        finally
        {
            // A session reset intentionally prevents an old lease from releasing
            // new-session state. This isolated fixture still owns its old gate.
            if (ClientSessionWork.Token != generation) fixture.Sync.Reset();
            // A reentrant replacement owns its binding; never overwrite it on exit.
            if (ReferenceEquals(ClientTransporterActions.Current, binding)) ClientTransporterActions.Attach(previous);
        }

        void Probe(string kind)
        {
            arrive(kind);
            if (ClientSessionWork.Token != generation || !ClientSessionWork.IsCurrent
                || !ReferenceEquals(ClientTransporterActions.Current, binding))
                throw new InvalidOperationException("Contention probe replaced its session or engine binding.");
        }
    }
}
