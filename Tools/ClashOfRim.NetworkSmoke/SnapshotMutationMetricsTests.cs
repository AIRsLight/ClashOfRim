using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AIRsLight.ClashOfRim.Network;

internal static class SnapshotMutationMetricsTests
{
    public static void Run()
    {
        var state = new ClashOfRimNetworkState();
        var samples = new ConcurrentBag<(string Name, string Operation, double Value)>();
        bool calledInsideLock = false;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == SnapshotMutationScope.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (Monitor.IsEntered(state.RaidSettlementSnapshotMutationGate)) calledInsideLock = true;
            string operation = tags.ToArray().Single(tag => tag.Key == "operation").Value!.ToString()!;
            samples.Add((instrument.Name, operation, value));
            if (operation == "throwing-listener") throw new IOException("injected diagnostics failure");
        });
        listener.Start();
        try
        {
            using (state.EnterSnapshotMutation("outer"))
            {
                using (state.EnterSnapshotMutation("nested")) { }
                throw new InvalidOperationException("business failure");
            }
        }
        catch (InvalidOperationException ex) when (ex.Message == "business failure") { }
        if (Monitor.IsEntered(state.RaidSettlementSnapshotMutationGate)
            || samples.Count(sample => sample.Operation == "outer") != 2
            || samples.Any(sample => sample.Operation == "nested"))
            throw new Exception("Snapshot metrics changed exception release or double-counted a nested mutation.");

        using var started = new ManualResetEventSlim();
        bool acquired = false;
        var waiter = new Thread(() =>
        {
            started.Set();
            using (state.EnterSnapshotMutation("contended")) acquired = true;
        });
        using (state.EnterSnapshotMutation("holder"))
        {
            waiter.Start();
            if (!started.Wait(TimeSpan.FromSeconds(5))
                || !SpinWait.SpinUntil(() => (waiter.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5))
                || acquired) throw new Exception("Snapshot mutation no longer excludes a competing thread.");
        }
        if (!waiter.Join(TimeSpan.FromSeconds(5)) || !acquired
            || !samples.Any(sample => sample.Operation == "contended" && sample.Name.EndsWith("wait") && sample.Value > 0))
            throw new Exception("Snapshot contention was not observed or released.");
        using (state.EnterSnapshotMutation("throwing-listener")) { }
        if (calledInsideLock || Monitor.IsEntered(state.RaidSettlementSnapshotMutationGate))
            throw new Exception("Metrics listener ran under the business lock or prevented release.");
    }
}
