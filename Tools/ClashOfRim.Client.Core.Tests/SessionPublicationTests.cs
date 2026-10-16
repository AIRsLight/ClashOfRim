using AIRsLight.ClashOfRim.ClientNetwork;

internal static class SessionPublicationTests
{
    public static async Task Run()
    {
        foreach (bool failed in new[] { false, true }) await SupersededPublication(failed);
        await ResetWaitsForPublication();
        await FailedPublicationReleasesGate();
    }

    private static async Task SupersededPublication(bool failed)
    {
        ClientSessionWork.Reset();
        var observed = Signal();
        var resume = Signal();
        string cache = "old";
        bool busy = true;
        Task old = ClientSessionWork.Run(async () =>
        {
            try
            {
                await ClientSessionWork.Observe(Task.FromResult("response"));
                observed.SetResult();
                await resume.Task;
                if (failed) throw new IOException("response processing failed");
                using var publication = ClientSessionWork.EnterPublication();
                cache = "stale response";
            }
            catch (IOException)
            {
                // Also exercises expiration between entering an error handler and publishing it.
                using var publication = ClientSessionWork.EnterPublication();
                cache = "stale error";
            }
            finally { ClientSessionWork.TryApply(() => busy = false); }
        });
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ClientSessionWork.Reset();
        cache = "new session";
        resume.SetResult();
        await old.WaitAsync(TimeSpan.FromSeconds(5));
        Require(cache == "new session" && busy, "Old response/error/completion crossed the session boundary.");
    }

    private static async Task ResetWaitsForPublication()
    {
        ClientSessionWork.Reset();
        var publishing = Signal();
        using var release = new ManualResetEventSlim();
        string cache = "old";
        Task writer = ClientSessionWork.Run(() =>
        {
            using var publication = ClientSessionWork.EnterPublication();
            publishing.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            Require(ClientSessionWork.TryApply(() => cache = "published"), "Nested publication lost its owner.");
            return Task.CompletedTask;
        });
        await publishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var resetStarted = Signal();
        Task reset = Task.Run(() =>
        {
            resetStarted.SetResult();
            ClientSessionWork.Reset();
            cache = "new session";
        });
        try
        {
            await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(await Task.WhenAny(reset, Task.Delay(100)) != reset, "Reset interleaved with a publication.");
        }
        finally { release.Set(); }
        await Task.WhenAll(writer, reset).WaitAsync(TimeSpan.FromSeconds(5));
        Require(cache == "new session", "A published response survived the following reset.");
    }

    private static async Task FailedPublicationReleasesGate()
    {
        ClientSessionWork.Reset();
        try
        {
            await ClientSessionWork.Run(() =>
            {
                using var publication = ClientSessionWork.EnterPublication();
                throw new IOException("injected publication failure");
            });
            throw new Exception("Active-session errors must remain visible.");
        }
        catch (IOException) { }
        await Task.Run(ClientSessionWork.Reset).WaitAsync(TimeSpan.FromSeconds(5));
        Require(ClientSessionWork.TryApply(() => { }), "Publication failure leaked the gate.");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
