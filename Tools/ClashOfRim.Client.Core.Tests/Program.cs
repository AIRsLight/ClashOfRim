using AIRsLight.ClashOfRim.ClientNetwork;

WireContractTests.Run();
Console.WriteLine("PASS: client/server JSON wire contracts");
LanguageHeaderTests.Run();
Console.WriteLine("PASS: ASCII language headers round-trip native labels, aliases and unsupported languages");

await SessionPublicationTests.Run();
Console.WriteLine("PASS: session publication, error and completion are atomic with reset (4 scenarios)");

await Check("late response cannot apply or clear a new request flag", async () =>
{
    ClientSessionWork.Reset();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var response = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    bool applied = false;
    bool newRequestInProgress = true;
    Task old = ClientSessionWork.Run(async () =>
    {
        started.SetResult();
        try { await ClientSessionWork.Observe(response.Task); applied = true; }
        catch (Exception) when (ClientSessionWork.IsCurrent) { throw; }
        finally { if (ClientSessionWork.IsCurrent) newRequestInProgress = false; }
    });
    await started.Task;
    ClientSessionWork.Reset();
    response.SetResult(42);
    await old;
    Require(!applied && newRequestInProgress);
});
await Check("queued actions expire and nested work keeps its owner", async () =>
{
    ClientSessionWork.Reset();
    Action? queued = null;
    bool applied = false;
    await ClientSessionWork.Run(() => { queued = ClientSessionWork.Guard(() => applied = true); return Task.CompletedTask; });
    ClientSessionWork.Reset();
    queued!();
    Require(!applied);
    await ClientSessionWork.Run(() => { ClientSessionWork.Guard(() => applied = true)(); return Task.CompletedTask; });
    Require(applied);
});
await Check("discarded queued work completes its waiter", async () =>
{
    ClientSessionWork.Reset();
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Action queued = ClientSessionWork.Guard(() => completion.SetResult(), () => completion.TrySetCanceled());
    ClientSessionWork.Reset();
    queued();
    Require(completion.Task.IsCanceled);
    await Task.CompletedTask;
});
await Check("exit cancels owned requests", async () =>
{
    ClientSessionWork.Reset();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    bool applied = false;
    Task task = ClientSessionWork.Run(async () =>
    {
        started.SetResult();
        await ClientSessionWork.Observe(Task.Delay(Timeout.Infinite, ClientSessionWork.Token));
        applied = true;
    });
    await started.Task;
    ClientSessionWork.Reset();
    await task.WaitAsync(TimeSpan.FromSeconds(5));
    Require(!applied);
});

static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static async Task Check(string name, Func<Task> test)
{
    await test();
    Console.WriteLine("PASS: " + name);
}
