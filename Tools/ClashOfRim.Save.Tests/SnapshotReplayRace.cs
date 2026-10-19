using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using static SnapshotTestAssertions;

internal static class SnapshotReplayRace
{
    public const int ParticipantCount = 12;

    public static T[] Run<T>(Func<int, Task<T>> call, Action beforeRelease)
    {
        var slots = (SemaphoreSlim)typeof(ClashOfRimNetworkServer).Assembly.GetType("AIRsLight.ClashOfRim.Network.SnapshotPreparationService")!
            .GetField("Slots", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        // .NET 8's synchronous semaphore waiter count confirms every endpoint reached preparation.
        // Keep this runtime-specific probe in tests; production has no scheduling/failure injection hook.
        var waitCount = typeof(SemaphoreSlim).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(int) && f.Name.Contains("waitCount", StringComparison.OrdinalIgnoreCase));
        int held = 0; while (slots.Wait(0)) held++;
        Check(held > 0, "preparation slots unexpectedly busy");
        var results = new T[ParticipantCount]; var errors = new Exception?[ParticipantCount]; var complete = new bool[ParticipantCount];
        var threads = Enumerable.Range(0, ParticipantCount).Select(i => new Thread(() =>
        {
            try { results[i] = call(i + 1).GetAwaiter().GetResult(); }
            catch (Exception ex) { errors[i] = ex; }
            finally { Volatile.Write(ref complete[i], true); }
        }) { IsBackground = true }).ToArray();
        try
        {
            foreach (var thread in threads) thread.Start();
            Check(SpinWait.SpinUntil(() => (int)waitCount.GetValue(slots)! == ParticipantCount || complete.Any(c => c), TimeSpan.FromSeconds(15))
                && (int)waitCount.GetValue(slots)! == ParticipantCount, "not every service request reached preparation: " + JsonSerializer.Serialize(results) + string.Join(";", errors.Select(e => e?.Message)));
            beforeRelease(); slots.Release(held); held = 0;
            foreach (var thread in threads) Check(thread.Join(TimeSpan.FromSeconds(30)), "service request timed out");
            if (errors.FirstOrDefault(e => e != null) is { } error) throw new Exception("service request failed", error);
            return results;
        }
        finally
        {
            if (held > 0) slots.Release(held);
            foreach (var thread in threads) if (thread.IsAlive) thread.Join(TimeSpan.FromSeconds(30));
        }
    }

}
