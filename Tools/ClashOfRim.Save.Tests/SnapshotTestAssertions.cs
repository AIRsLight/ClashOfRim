internal static class SnapshotTestAssertions
{
    public static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    public static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception(message);
    }

    public static void GateReleased(object gate, string message) => Task.Run(() =>
    {
        Check(Monitor.TryEnter(gate, TimeSpan.FromSeconds(3)), message);
        Monitor.Exit(gate);
    }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
}
