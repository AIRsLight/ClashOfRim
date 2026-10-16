using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Fixture = HttpSnapshotRecoveryTests.Fixture;

internal static class FileLoggerLifetimeTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        string? configuredLogDirectory = Environment.GetEnvironmentVariable("CLASH_OF_RIM_LOG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLASH_OF_RIM_LOG_DIR", null);
            RunIsolated(factory);
        }
        finally { Environment.SetEnvironmentVariable("CLASH_OF_RIM_LOG_DIR", configuredLogDirectory); }
    }

    private static void RunIsolated(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (bool start in new[] { false, true }) WithRoot(root => VerifyHost(root, start));
        foreach (bool malformed in new[] { false, true }) WithRoot(root => VerifyEarlyFailure(root, malformed));
        using (var f = new Fixture(factory))
        {
            var priorLogger = new CountingLogger(); f.State.SetRuntimeLogger(priorLogger);
            f.Seed(); f.Store.FailPreparation = true;
            bool failed = false;
            try { f.Start(); } catch (IOException) { failed = true; }
            Require(failed && f.State.SnapshotCommits.HasPending, "Startup recovery failure was not retained.");
            Exclusive(LogPath(Path.GetDirectoryName(f.Database)!));
            Require(ReferenceEquals(f.State.RuntimeLogger, priorLogger), "Failed host replaced the supplied state's original logger.");
            f.State.RuntimeLogger.LogWarning("late runtime log after failed startup");
            Require(priorLogger.Messages == 1, "Restored runtime logger no longer works.");
            f.Store.FailPreparation = false; f.Start(); f.AssertRecovered();
        }
        WithRoot(VerifyLateWriter);
        WithRoot(VerifyConcurrentShutdown);
    }

    private static void VerifyHost(string root, bool start)
    {
        PrepareLocalization(root);
        string path = LogPath(root);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            var app = Build(root); var provider = app.Services.GetServices<ILoggerProvider>().OfType<ClashOfRimFileLoggerProvider>().Single();
            ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("lifetime-proof");
            ILogger fileLogger = provider.CreateLogger("retained-file-logger");
            try
            {
                if (start)
                {
                    app.StartAsync().GetAwaiter().GetResult();
                    using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                    using var response = client.GetAsync("/server/plugins").GetAwaiter().GetResult(); response.EnsureSuccessStatusCode();
                }
                logger.LogWarning(new EventId(17, "proof"), new InvalidOperationException("exception-proof"), "before shutdown {Cycle}", cycle);
                if (start) app.StopAsync().GetAwaiter().GetResult();
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Exclusive(path);
                string before = File.ReadAllText(path);
                Require(before.Contains("[Warning] lifetime-proof 17:proof - before shutdown " + cycle)
                    && before.Contains("exception-proof"), "Shutdown lost formatted or exception logs.");
                fileLogger.LogWarning("late file log");
                provider.Dispose(); // Idempotent even after host ownership released it.
                Require(File.ReadAllText(path) == before && !fileLogger.IsEnabled(LogLevel.Warning), "Disposed file logger still wrote or enabled output.");
            }
            finally
            {
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                // Clean up the old leaking implementation too when this regression fails.
                provider.Dispose();
            }
        }
    }

    private static void VerifyEarlyFailure(string root, bool malformed)
    {
        if (malformed)
        {
            Directory.CreateDirectory(Path.Combine(root, "Localization"));
            File.WriteAllText(Path.Combine(root, "Localization", "English.json"), "{");
        }
        bool failed = false;
        var state = new ClashOfRimNetworkState(); var priorLogger = new CountingLogger(); state.SetRuntimeLogger(priorLogger);
        try { Build(root, state); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException) { failed = true; }
        Require(failed, "Invalid localization did not fail server construction.");
        Require(ReferenceEquals(state.RuntimeLogger, priorLogger), "Early validation failure replaced an existing logger.");
        Require(!File.Exists(LogPath(root)), "Pre-host validation opened the log file before ownership existed.");
        PrepareLocalization(root);
        var retry = Build(root); retry.DisposeAsync().AsTask().GetAwaiter().GetResult(); Exclusive(LogPath(root));
    }

    private static void VerifyLateWriter(string root)
    {
        string path = Path.Combine(root, "direct.log");
        using var provider = new ClashOfRimFileLoggerProvider(path);
        ILogger logger = provider.CreateLogger("writer");
        Require(ReferenceEquals(logger, provider.CreateLogger("writer")), "Live logger category cache changed.");
        logger.LogInformation("before shutdown");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => logger.Log(LogLevel.Information, new EventId(1), "late", null, (state, _) =>
        {
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Writer was not released."); return state;
        }));
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(10)), "Writer did not enter formatting.");
            provider.Dispose(); Exclusive(path);
        }
        finally { release.Set(); writer.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
        logger.Log(LogLevel.Information, new EventId(2), "late", null, (_, _) => throw new Exception("Disposed logger still formatted messages."));
        provider.CreateLogger("after-disposal").LogWarning("ignored");
        Require(!logger.IsEnabled(LogLevel.Warning) && File.ReadAllLines(path).Length == 1, "Late writer appended after shutdown.");
    }

    private static void VerifyConcurrentShutdown(string root)
    {
        string path = Path.Combine(root, "concurrent.log");
        using var provider = new ClashOfRimFileLoggerProvider(path);
        var logger = provider.CreateLogger("parallel"); logger.LogInformation("record-END");
        Parallel.For(0, 16, worker =>
        {
            for (int i = 0; i < 100; i++)
            {
                if (worker % 4 == 0 && i == 50) provider.Dispose();
                logger.LogInformation("record-{Worker}-{Index}-END", worker, i);
            }
        });
        provider.Dispose(); Exclusive(path);
        Require(File.ReadAllLines(path).All(line => line.EndsWith("-END", StringComparison.Ordinal)), "Concurrent disposal split a log record.");
    }

    private static WebApplication Build(string root, ClashOfRimNetworkState? state = null) => ClashOfRimNetworkServer.Build(
        ["--contentRoot", root, "--urls", "http://127.0.0.1:0", "--language=English", "--Logging:LogLevel:Default", "Warning"], state ?? new());
    private static string LogPath(string root) => Path.Combine(root, "Logs", $"server-{DateTime.UtcNow:yyyyMMdd}.log");
    private static void PrepareLocalization(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Localization"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestLocalization", "English.json"), Path.Combine(root, "Localization", "English.json"), true);
    }
    private static void Exclusive(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new Exception("Disposed or failed host retained the log file handle.", ex); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class CountingLogger : ILogger
    {
        public int Messages;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Interlocked.Increment(ref Messages);
    }
    private static void WithRoot(Action<string> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-logger-lifetime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        bool failed = true;
        try { run(root); failed = false; }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) when (failed) { /* Preserve the original failure if the old implementation leaked a handle. */ }
        }
    }
}
