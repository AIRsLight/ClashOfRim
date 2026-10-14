using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AIRsLight.ClashOfRim.Network;

// Synchronous scope only: retain the existing reentrant Monitor and lock ordering.
internal sealed class SnapshotMutationScope : IDisposable
{
    internal const string MeterName = "ClashOfRim.SnapshotMutation";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Wait = Meter.CreateHistogram<double>("snapshot.mutation.wait", "ms");
    private static readonly Histogram<double> Hold = Meter.CreateHistogram<double>("snapshot.mutation.hold", "ms");
    private object? gate;
    private readonly string operation;
    private readonly bool measured;
    private readonly long acquired;
    private readonly double waitMilliseconds;

    internal SnapshotMutationScope(object gate, string operation)
    {
        this.operation = operation;
        // Nested recovery/post-upload calls belong to the outer transaction, not new samples.
        measured = (Wait.Enabled || Hold.Enabled) && !Monitor.IsEntered(gate);
        long started = measured ? Stopwatch.GetTimestamp() : 0;
        Monitor.Enter(gate);
        this.gate = gate;
        if (measured)
        {
            acquired = Stopwatch.GetTimestamp();
            waitMilliseconds = Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds;
        }
    }

    public void Dispose()
    {
        object? ownedGate = Interlocked.Exchange(ref gate, null);
        if (ownedGate is null) return;
        double holdMilliseconds = measured ? Stopwatch.GetElapsedTime(acquired).TotalMilliseconds : 0;
        Monitor.Exit(ownedGate);
        if (!measured) return;
        // Listener code must run after release and cannot turn a committed mutation into an error.
        try { Wait.Record(waitMilliseconds, new KeyValuePair<string, object?>("operation", operation)); }
        catch (Exception) { /* A diagnostics subscriber cannot affect business completion. */ }
        try { Hold.Record(holdMilliseconds, new KeyValuePair<string, object?>("operation", operation)); }
        catch (Exception) { /* A diagnostics subscriber cannot affect business completion. */ }
    }
}
