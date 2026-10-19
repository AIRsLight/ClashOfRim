using System.Text.Json;
using ClashOfRim.GameTests;
using ClashOfRim.TestSupport;
using RimTest.Client;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameScenarioPollingTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        Boundaries();
        foreach (string language in new[] { "English", "中文", "Français", "Türkçe" }) await DialogChoices(language);
        await Compatibility(); await Pauses(); await Operations(); await BusyStates();
    }

    private static void Boundaries()
    {
        var roots = new[] { typeof(ScenarioPolling), typeof(ScenarioDialogs), typeof(ScenarioAssertions) };
        bool Owned(Type type) => roots.Contains(type) || type.DeclaringType is { } parent && Owned(parent);
        var violations = ModuleDependencyGuard.FindViolations(typeof(GameSuite).Assembly.GetTypes().Where(Owned).ToArray(),
            type => type == typeof(GameSuite) || type == typeof(SuiteProfile)
                || type.Namespace?.StartsWith("AIRsLight.ClashOfRim.Network", StringComparison.Ordinal) == true
                || type == typeof(IServiceProvider));
        Check(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static async Task DialogChoices(string language)
    {
        using var f = await Fixture.Create();
        string notice = language + ": Steam missing", ignore = language + ": Ignore", quit = language + ": Quit";
        bool steam = true, badChoice = false;
        f.Script = (_, method, _) => method switch
        {
            "clash.labels" => new { steamClientMissing = notice, ignore, quit },
            "dialogs.messages" => new { items = new[] { new { reference = new { handle = "dialog" }, text = steam ? notice : "confirmation",
                buttons = new[] { new { id = "A", label = steam || badChoice ? quit : "Confirm" }, new { id = "B", label = ignore } } } } },
            _ => null
        };
        await f.Dialogs.Confirm("A");
        Check(f.Calls.Last().Method == "dialogs.choose" && f.Calls.Last().Args.String("button") == "B", "Localized missing-Steam dialog selected Quit.");
        steam = false;
        await f.Dialogs.Confirm("B");
        Check(f.Calls.Last().Client == "B" && f.Calls.Last().Args.String("button") == "A", "Ordinary confirmation changed client or button.");
        int selected = f.Count("dialogs.choose"); badChoice = true;
        await Reject<AssertionException>(() => f.Dialogs.Confirm("A"));
        Check(f.Count("dialogs.choose") == selected, "Unexpected Quit dialog was activated.");
    }

    private static async Task Compatibility()
    {
        foreach (bool allowLanguage in new[] { false, true })
        foreach (bool open in new[] { false, true })
        foreach (bool canContinue in new[] { false, true })
        {
            using var f = await Fixture.Create(allowLanguage);
            f.Script = (_, method, _) => method == "clash.compatibility" ? new { open, canContinue } : null;
            if (open && (!allowLanguage || !canContinue))
                await Reject<AssertionException>(async () => { await f.Dialogs.AcceptLanguageWarning("A"); });
            else Check(await f.Dialogs.AcceptLanguageWarning("A") == open, "Compatibility observation returned wrong result.");
            Check(f.Count("clash.compatibility.continue") == (open && allowLanguage && canContinue ? 1 : 0), "Compatibility warning bypassed its permissions.");
        }
    }

    private static async Task Pauses()
    {
        foreach (string method in new[] { "time.status", "clash.state", "clash.gravship" })
        foreach (bool running in new[] { false, true })
        foreach (bool paused in new[] { false, true })
        foreach (bool forced in new[] { false, true })
        {
            using var f = await Fixture.Create();
            var value = new { state = running ? "running" : "completed", paused, forcedPause = forced };
            f.Script = (_, query, _) => query == "time.status" ? value : null;
            var options = f.Polling.Waiting("B", method, 37);
            Check(options.Timeout == TimeSpan.FromSeconds(37), "Scenario timeout changed.");
            await options.AfterUnmet!(Wire.Element(value), default);
            Check(f.Count("time.resume") == (running && paused && !forced ? 1 : 0), "Polling resumed a forced pause or missed an ordinary pause.");
            Check(f.Count("time.status") == (method == "time.status" ? 0 : 1), "Time polling performed a redundant observation.");
            Check(f.Count("clash.compatibility") == (method == "clash.state" ? 1 : 0)
                && f.Count("dialogs.messages") == (method == "clash.gravship" ? 1 : 0), "A wait handled another domain's dialogs.");
            Check(f.Calls.All(c => c.Client == "B"), "Polling affected the other client.");
        }
        using var cancelled = await Fixture.Create();
        using var token = new CancellationTokenSource(); token.Cancel();
        await Reject<OperationCanceledException>(() => cancelled.Polling.Waiting("A", "time.status").AfterUnmet!(
            Wire.Element(new { state = "running", paused = true, forcedPause = false }), token.Token));
        Check(cancelled.Count("time.resume") == 0, "Cancelled polling issued a resume.");
    }

    private static async Task Operations()
    {
        using (var f = await Fixture.Create())
        {
            int observations = 0;
            f.Script = (_, method, _) => method switch
            {
                "time.step" => new { id = "current" },
                "time.status" => new { id = ++observations == 1 ? "older" : "current", state = "completed", advancedTicks = 60 },
                _ => null
            };
            await f.Polling.Step("A", 60);
            Check(observations == 2 && f.Count("time.step") == 1, "Stepping accepted another operation's completion or retried the action.");
        }
        foreach (string state in new[] { "failed", "cancelled" })
        {
            using var f = await Fixture.Create();
            f.Script = (_, method, _) => method switch
            { "time.step" => new { id = "current" }, "time.status" => new { id = "current", state }, _ => null };
            await Reject<OperationException>(() => f.Polling.Step("A", 60));
            Check(f.Count("time.step") == 1, "A failed tick operation was resubmitted.");
        }
        using var incomplete = await Fixture.Create();
        incomplete.Script = (_, method, _) => method switch
        { "time.step" => new { id = "current" }, "time.status" => new { id = "current", state = "completed", advancedTicks = 59 }, _ => null };
        await Reject<AssertionException>(() => incomplete.Polling.Step("A", 60));
    }

    private static async Task BusyStates()
    {
        foreach (string busy in new[] { "manualSyncInProgress", "snapshotUploadInProgress", "localAtomicMutationPending", "bankInProgress",
            "serverShopInProgress", "mercenaryInProgress", "adminInProgress", "tradeOrdersPageLoadInProgress" })
        {
            using var f = await Fixture.Create(); int polls = 0;
            f.Script = (_, method, _) => method == "clash.state" ? new Dictionary<string, bool> { [busy] = ++polls == 1 } : null;
            await f.Polling.Idle("A");
            Check(polls == 2, "Idle skipped busy state: " + busy);
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "ClashScenarioPolling-" + Guid.NewGuid().ToString("N"));
        internal readonly List<(string Client, string Method, JsonElement Args)> Calls = new();
        internal readonly TestRun Run;
        internal readonly ScenarioDialogs Dialogs;
        internal readonly ScenarioPolling Polling;
        internal Func<string, string, JsonElement, object?>? Script;
        private Fixture(bool allowLanguage)
        { Run = new(path, new[] { "unit" }); Dialogs = new(Run, allowLanguage); Polling = new(Run, Dialogs); }
        internal static async Task<Fixture> Create(bool allowLanguage = false)
        {
            var f = new Fixture(allowLanguage);
            await f.Run.AddClientAsync("A", new Connection(f, "A")); await f.Run.AddClientAsync("B", new Connection(f, "B"));
            f.Calls.Clear(); return f;
        }
        internal int Count(string method) => Calls.Count(c => c.Method == method);
        public void Dispose() => Directory.Delete(path, recursive: true);
        private sealed class Connection(Fixture owner, string client) : IRimTestConnection
        {
            public Task<JsonElement> CallAsync(string method, object? arguments = null, int timeoutMs = 10000, CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                var args = Wire.Element(arguments ?? new { }); owner.Calls.Add((client, method, args));
                object result = owner.Script?.Invoke(client, method, args) ?? (method switch
                {
                    "runtime.hello" => new { capabilities = new[] { "time.status", "clash.state", "clash.gravship" }.Select(name => new Capability(name, name, "observe")).ToArray() },
                    "clash.compatibility" => new { open = false },
                    "dialogs.messages" => new { items = Array.Empty<object>() },
                    "time.status" => new { state = "completed" },
                    _ => new { }
                });
                return Task.FromResult(Wire.Element(result));
            }
        }
    }
}
