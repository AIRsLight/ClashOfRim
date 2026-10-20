using System.Diagnostics;
using System.Reflection;
using AIRsLight.ClashOfRim.ClientNetwork;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ClashOfRim.RimTestBridge;

// Test-only probes: Root callbacks continue to be observed even when the RimTest
// command pump intentionally waits for a long event. No timing assertion uses RPC latency.
internal static class RemoteMapPerformanceObservation
{
    private static readonly object gate = new();
    private static bool installed;
    private static Sample? active;
    private sealed class Sample
    {
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly List<double> Updates = [], Repaints = [];
        internal readonly List<object> Spans = [];
        internal readonly HashSet<int> UpdateThreads = [], RepaintThreads = [];
        internal object? Payload;
        internal bool Truncated;
    }
    private sealed record Span(Sample Sample, string Name, double StartMs, int Thread);

    internal static void Start()
    {
        Install();
        lock (gate) active = new Sample();
    }

    internal static object Stop()
    {
        lock (gate)
        {
            var sample = active ?? throw new InvalidOperationException("Performance observation was not started.");
            active = null;
            return new
            {
                durationMs = sample.Clock.Elapsed.TotalMilliseconds, sample.Truncated, sample.Payload,
                updates = Frames(sample.Updates, sample.UpdateThreads),
                repaints = Frames(sample.Repaints, sample.RepaintThreads), spans = sample.Spans.ToArray(),
                map = Current.Game?.CurrentMap is { } map ? new
                {
                    width = map.Size.x, height = map.Size.z, things = map.listerThings.AllThings.Count,
                    pawns = map.mapPawns.AllPawnsSpawned.Count
                } : null,
                screen = new { Screen.width, Screen.height }
            };
        }
    }

    private static object Frames(List<double> times, HashSet<int> threads)
    {
        var gaps = times.Zip(times.Skip(1), (start, end) => new { startMs = start, endMs = end, durationMs = end - start }).ToArray();
        var sorted = gaps.Select(g => g.durationMs).Order().ToArray();
        return new
        {
            count = times.Count, threads = threads.ToArray(), timesMs = times.ToArray(),
            maxGapMs = sorted.LastOrDefault(), p95GapMs = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)],
            stalls = gaps.Where(g => g.durationMs >= 100).ToArray()
        };
    }

    private static void Install()
    {
        if (installed) return;
        var harmony = new Harmony("ClashOfRim.RimTest.RemoteMapPerformance");
        harmony.Patch(AccessTools.Method(typeof(Root), "Update"), prefix: new HarmonyMethod(typeof(RemoteMapPerformanceObservation), nameof(Update)));
        harmony.Patch(AccessTools.Method(typeof(Root), "OnGUI"), prefix: new HarmonyMethod(typeof(RemoteMapPerformanceObservation), nameof(Repaint)));
        foreach (var (type, method) in new[]
        {
            ("AIRsLight.ClashOfRim.Modules.Maps.ClientObservationMaps", "OpenAsync"),
            ("AIRsLight.ClashOfRim.RemoteMaps.ClientObservationRuntime", "Open"),
            ("AIRsLight.ClashOfRim.RemoteMaps.RemoteMapSnapshotProjector", "TryProjectPrepared"),
            ("AIRsLight.ClashOfRim.Modules.Maps.ClientRemoteSnapshotDocument", "Read"),
            ("AIRsLight.ClashOfRim.Modules.Snapshots.ClientSnapshotPayloadCodec", "TryDecode"),
            ("AIRsLight.ClashOfRim.RemoteMaps.RemoteMapRuntimeLoader", "TryLoad"),
            ("AIRsLight.ClashOfRim.RemoteMaps.RemoteMapRuntimeLoader", "TryLoadFile"),
            ("Verse.ScribeLoader", "InitLoading"), ("Verse.ScribeLoader", "FinalizeLoading"),
            ("Verse.Map", "FinalizeLoading"),
            ("Verse.MapDrawer", "RegenerateEverythingNow"),
            ("Verse.LongEventHandler", "ExecuteToExecuteWhenFinished"),
            ("AIRsLight.ClashOfRim.RemoteMaps.RemoteObservationMapPostProcessor", "Apply")
        })
            harmony.Patch(Required(type, method), prefix: new HarmonyMethod(typeof(RemoteMapPerformanceObservation), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(RemoteMapPerformanceObservation), nameof(End)));
        harmony.Patch(Required("AIRsLight.ClashOfRim.Modules.Maps.ClientRemoteSnapshotDownload", "DownloadAsync"),
            prefix: new HarmonyMethod(typeof(RemoteMapPerformanceObservation), nameof(Begin)),
            postfix: new HarmonyMethod(typeof(RemoteMapPerformanceObservation), nameof(EndAsync)));
        harmony.Patch(Required("AIRsLight.ClashOfRim.RemoteMaps.RemoteMapSnapshotProjector", "TryProjectPrepared"),
            prefix: new HarmonyMethod(typeof(RemoteMapPerformanceObservation), nameof(Payload)));
        installed = true;
    }

    private static MethodInfo Required(string type, string method) =>
        AccessTools.Method(AccessTools.TypeByName(type), method) ?? throw new MissingMethodException(type, method);
    private static void Update() => Frame(false);
    private static void Repaint() { if (Event.current?.type == EventType.Repaint) Frame(true); }
    private static void Frame(bool repaint)
    {
        lock (gate)
        {
            if (active is not { } sample) return;
            var times = repaint ? sample.Repaints : sample.Updates;
            if (times.Count == 20000) { sample.Truncated = true; return; }
            times.Add(sample.Clock.Elapsed.TotalMilliseconds);
            (repaint ? sample.RepaintThreads : sample.UpdateThreads).Add(Environment.CurrentManagedThreadId);
        }
    }
    private static void Begin(MethodBase __originalMethod, out Span? __state)
    {
        lock (gate) __state = active is { } sample ? new Span(sample,
            __originalMethod.DeclaringType!.Name + "." + __originalMethod.Name,
            sample.Clock.Elapsed.TotalMilliseconds, Environment.CurrentManagedThreadId) : null;
    }
    private static void End(Span? __state, Exception? __exception) => Finish(__state, __exception?.GetType().Name);
    private static void EndAsync(Span? __state, Task __result)
    {
        if (__state is null) return;
        _ = __result.ContinueWith(t => Finish(__state, t.IsFaulted ? "faulted" : t.IsCanceled ? "cancelled" : null),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static void Finish(Span? span, string? error)
    {
        if (span is null) return;
        lock (gate)
        {
            if (!ReferenceEquals(active, span.Sample)) return;
            if (active.Spans.Count == 512) { active.Truncated = true; return; }
            double end = active.Clock.Elapsed.TotalMilliseconds;
            active.Spans.Add(new { span.Name, startMs = span.StartMs, endMs = end, durationMs = end - span.StartMs,
                startThread = span.Thread, endThread = Environment.CurrentManagedThreadId, error });
        }
    }
    private static void Payload(ModSnapshotPackageMetadataDto __0, byte[] __1)
    {
        lock (gate) if (active is { } sample)
            sample.Payload = new { compressedBytes = __1.LongLength, originalSaveBytes = __0.OriginalSaveBytes };
    }
}
