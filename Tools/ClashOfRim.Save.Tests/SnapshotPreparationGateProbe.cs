internal static class SnapshotPreparationGateProbe
{
    public static void Run(object gate, Action<Action?> setHook, Action operation, Action whilePreparing)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        setHook(() =>
        {
            if (Monitor.IsEntered(gate)) throw new Exception("Snapshot preparation holds the business mutation gate.");
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new Exception("Preparation probe was not released.");
        });
        var worker = Task.Run(operation);
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10)))
            {
                if (worker.IsFaulted) worker.GetAwaiter().GetResult();
                throw new Exception("The operation did not prepare its storage payload.");
            }
            if (!Monitor.TryEnter(gate, TimeSpan.FromSeconds(3))) throw new Exception("Another thread cannot enter during preparation.");
            try { setHook(null); whilePreparing(); }
            finally { Monitor.Exit(gate); }
        }
        finally
        {
            release.Set();
            worker.WaitAsync(TimeSpan.FromSeconds(25)).GetAwaiter().GetResult();
        }
    }
}
