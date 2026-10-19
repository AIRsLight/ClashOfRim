using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using ClashOfRim.GameTests;
using RimTest.Client;
using RimTest.Protocol;
using RimTest.Testing;

internal sealed class GameScenarioTestRuntime : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "ClashScenarioUnit-" + Guid.NewGuid().ToString("N"));
    internal readonly List<(string Client, string Method, JsonElement Args)> Calls = new();
    internal readonly TestRun Run;
    internal readonly ScenarioDialogs Dialogs;
    internal readonly ScenarioPolling Polling;
    internal Func<string, string, JsonElement, object?>? Script;
    internal bool Headless = true;
    internal readonly Dictionary<string, List<JsonElement>> Logs = new() { ["A"] = [], ["B"] = [] };
    private GameScenarioTestRuntime(bool allowLanguage, IEnumerable<string>? plannedCases)
    { Run = new(path, plannedCases ?? ["unit"]); Dialogs = new(Run, allowLanguage); Polling = new(Run, Dialogs); }
    internal static async Task<GameScenarioTestRuntime> Create(bool allowLanguage = false, IEnumerable<string>? plannedCases = null)
    {
        var f = new GameScenarioTestRuntime(allowLanguage, plannedCases);
        await f.Run.AddClientAsync("A", new Connection(f, "A")); await f.Run.AddClientAsync("B", new Connection(f, "B"));
        f.Calls.Clear(); return f;
    }
    internal int Count(string method) => Calls.Count(c => c.Method == method);
    internal ServerEntryScenario CreateEntry(Func<string>? address = null, Func<string, bool>? isUserOnline = null, WorldScenario? world = null)
        => new(Run, Polling, Dialogs,
            world ?? new(Run, Polling, new(), new(), new StraightLineWorldTileDistanceCalculator(), _ => throw new InvalidOperationException("Unexpected disk probe.")),
            new(Run, Polling), new(Run), address ?? (() => "http://test-server"), isUserOnline ?? (_ => false));
    public void Dispose() => Directory.Delete(path, recursive: true);
    private sealed class Connection(GameScenarioTestRuntime owner, string client) : IRimTestConnection
    {
        public Task<JsonElement> CallAsync(string method, object? arguments = null, int timeoutMs = 10000, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var args = Wire.Element(arguments ?? new { }); owner.Calls.Add((client, method, args));
            object result = owner.Script?.Invoke(client, method, args) ?? (method switch
            {
                "runtime.hello" => new { headless = owner.Headless, capabilities = new[] { "time.status", "clash.state", "clash.gravship", "game.state", "clash.difficulty", "clash.world.details", "caravans.list", "caravan.state", "clash.events", "clash.records", "clash.remote", "clash.inventory", "clash.support.assignments", "clash.mercenaries", "clash.bank", "dialogs.messages", "clash.admin" }.Select(name => new Capability(name, name, "observe")).ToArray() },
                "clash.compatibility" => new { open = false },
                "dialogs.messages" or "dialogs.nodes" or "pages.list" => new { items = Array.Empty<object>() },
                "runtime.logs" => new { items = owner.Logs[client].Where(e => e.Int("sequence") > (args.TryGetProperty("after", out var after) ? after.GetInt32() : 0)).ToArray() },
                "time.step" => new { id = "unit-step" },
                "time.status" => new { id = "unit-step", state = "completed", advancedTicks = owner.Calls.LastOrDefault(c => c.Client == client && c.Method == "time.step").Args is { ValueKind: JsonValueKind.Object } step ? step.Int("ticks") : 0 },
                _ => new { }
            });
            return Task.FromResult(Wire.Element(result));
        }
    }
}
