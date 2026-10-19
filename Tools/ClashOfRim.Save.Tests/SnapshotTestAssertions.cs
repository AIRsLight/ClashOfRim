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
}
