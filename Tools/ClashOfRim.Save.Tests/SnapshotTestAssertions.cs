internal static class SnapshotTestAssertions
{
    public static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
