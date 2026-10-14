using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

internal static class SnapshotContentionProbe
{
    internal static async Task RunAsync(string outputPath,
        Func<string, string, string, string?, string?, long, int, SaveSnapshotPackage> fixture)
    {
        var results = new List<object>();
        foreach (int things in new[] { 0, 5000 })
            foreach (int concurrency in new[] { 1, 4, 8 })
                results.Add(await MeasureAsync(concurrency, things, fixture));
        string fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await File.WriteAllTextAsync(fullOutput, JsonSerializer.Serialize(new
        {
            createdAtUtc = DateTimeOffset.UtcNow,
            runtime = RuntimeInformation.FrameworkDescription,
            operatingSystem = RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount,
            scope = "Synthetic loopback HTTP uploads; file snapshots and journals, SQLite bank/ledger; other registries in memory. Five measured waves after one warm-up wave. Client package creation is excluded. Not a production throughput estimate or a gameplay test.",
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("提交锁测量已保存：" + fullOutput);
    }

    private static async Task<object> MeasureAsync(int concurrency, int things,
        Func<string, string, string, string?, string?, long, int, SaveSnapshotPackage> fixture)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-contention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            var state = new ClashOfRimNetworkState(
                ledger: new SqliteAuthoritativeEventLedger(database),
                snapshotStore: new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")),
                bankLoans: new BankLoanRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.BankLoans), null),
                snapshotCommits: new SnapshotCommitService(Path.Combine(root, "intents")));
            var tokens = new string[concurrency];
            long originalBytes = 0, payloadBytes = 0;
            for (int index = 0; index < concurrency; index++)
            {
                string user = "probe-user-" + index, colony = "probe-colony-" + index;
                SaveSnapshotPackage package = fixture(user, colony, "initial", null, null, 1000, things);
                var result = new SnapshotUploadReceiver(state.SnapshotStore).Receive(
                    new SnapshotUploadContext(user, colony, "initial"), package, DateTimeOffset.UtcNow);
                if (!result.Accepted) throw new Exception("Probe baseline rejected: " + result.Message);
                state.LoginSessions.TryCreate(user, colony, DateTimeOffset.UtcNow, out string session);
                tokens[index] = state.AuthTokens.Issue(user, user, colony, session, DateTimeOffset.UtcNow);
                originalBytes = package.Envelope.OriginalSaveBytes;
                payloadBytes = package.Payload.Length;
            }
            var measurements = new ConcurrentBag<(string Instrument, string Operation, double Value)>();
            bool collecting = false;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == SnapshotMutationScope.MeterName) owner.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                if (!Volatile.Read(ref collecting)) return;
                string operation = tags.ToArray().Single(tag => tag.Key == "operation").Value!.ToString()!;
                measurements.Add((instrument.Name, operation, value));
            });
            listener.Start();
            await using var app = ClashOfRimNetworkServer.Build(Array.Empty<string>(), state);
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(120) };
                var client = new ClashOfRimNetworkClient(http);
                var latencies = new ConcurrentBag<double>();
                double elapsedMs = 0;
                for (int wave = 0; wave < 6; wave++)
                {
                    // Prepare packages and pending business before timing, to isolate server work.
                    var packages = new SaveSnapshotPackage[concurrency];
                    for (int index = 0; index < concurrency; index++)
                    {
                        string user = "probe-user-" + index, colony = "probe-colony-" + index;
                        var previous = state.SnapshotStore.GetLatest(user, colony)!;
                        packages[index] = fixture(user, colony, "wave-" + wave, previous.Identity.SnapshotId,
                            previous.Envelope.NextLineageToken, 1100 + wave * 100, things);
                        state.BankLoans.CreateDebt($"pending-{index}-{wave}", user, colony, previous.Identity.SnapshotId!,
                            10, "Probe", "probe", "probe", 1000, DateTimeOffset.UtcNow, pendingActivation: true);
                    }
                    Volatile.Write(ref collecting, wave > 0);
                    long started = Stopwatch.GetTimestamp();
                    await Task.WhenAll(packages.Select(async (package, index) =>
                    {
                        long requestStarted = Stopwatch.GetTimestamp();
                        var identity = package.Envelope.Identity;
                        UploadSnapshotResponse response = await client.UploadSnapshotAsync(new UploadSnapshotMetadataRequest(
                            $"upload-{index}-{wave}", identity.OwnerId, identity.ColonyId, identity.SnapshotId!,
                            ProtocolDtoMapper.ToMetadataDto(package), tokens[index]), package.Payload);
                        if (!response.Result.Accepted || response.AcceptedSnapshotId != identity.SnapshotId
                            || string.IsNullOrEmpty(response.NextLineageToken))
                            throw new Exception("Probe upload rejected: " + response.Result.Message);
                        if (wave > 0) latencies.Add(Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds);
                    }));
                    if (wave > 0) elapsedMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Volatile.Write(ref collecting, false);
                }
                for (int index = 0; index < concurrency; index++)
                {
                    if (state.SnapshotStore.GetLatest("probe-user-" + index, "probe-colony-" + index)?.Identity.SnapshotId != "wave-5"
                        || state.BankLoans.GetOpenDebts("probe-user-" + index, "probe-colony-" + index).Count(debt => debt.Status == BankLoanRegistry.StatusActive) != 6)
                        throw new Exception("Probe lost a confirmed snapshot or pending business mutation.");
                }
                if (Directory.EnumerateFiles(Path.Combine(root, "intents"), "*.json").Any())
                    throw new Exception("Probe left an incomplete commit.");
                return new
                {
                    concurrency, extraThings = things, originalBytes, payloadBytes, acceptedRequests = concurrency * 5,
                    measuredHttpMilliseconds = Math.Round(elapsedMs, 2),
                    requestsPerMeasuredSecond = Math.Round(concurrency * 5 * 1000 / elapsedMs, 2),
                    httpLatency = Summarize(latencies),
                    locks = measurements.GroupBy(sample => new { sample.Operation, sample.Instrument })
                        .OrderBy(group => group.Key.Operation).ThenBy(group => group.Key.Instrument)
                        .Select(group => new { operation = group.Key.Operation, instrument = group.Key.Instrument,
                            milliseconds = Summarize(group.Select(sample => sample.Value)) }).ToArray()
                };
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static object Summarize(IEnumerable<double> samples)
    {
        double[] ordered = samples.Order().ToArray();
        double Percentile(double percentile) => Math.Round(ordered[Math.Max(0, (int)Math.Ceiling(percentile * ordered.Length) - 1)], 3);
        return new { count = ordered.Length, total = Math.Round(ordered.Sum(), 3), p50 = Percentile(.5), p95 = Percentile(.95), max = Percentile(1) };
    }
}
