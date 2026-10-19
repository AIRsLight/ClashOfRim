using System;
using Verse;

// Real Scribe loads use DeepProfiler, but the console runner has no game Prefs.
// Disable profiling instead of patching a getter that may already be inlined.
// The caller still owns the Scribe session and its cleanup.
internal sealed class NativeScribeTestScope : IDisposable
{
    private readonly bool profiling = DeepProfiler.enabled;

    internal NativeScribeTestScope() => DeepProfiler.enabled = false;

    public void Dispose() => DeepProfiler.enabled = profiling;
}
