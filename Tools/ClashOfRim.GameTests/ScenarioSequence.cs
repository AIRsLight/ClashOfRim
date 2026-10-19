using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed record ScenarioStep(string Id, bool Report = true, bool ContinueOnFailure = false);

// Describes ordering/reporting only. Domain state stays in the bound closures.
internal sealed class ScenarioGroup(string name, params ScenarioStep[] steps)
{
    internal string Name { get; } = name;
    internal IReadOnlyList<ScenarioStep> Steps { get; } = Array.AsReadOnly(steps.ToArray());
    internal IEnumerable<string> CaseIds => Steps.Where(s => s.Report).Select(s => s.Id);
    internal static ScenarioGroup Cases(string name, params string[] ids) => new(name, ids.Select(id => new ScenarioStep(id)).ToArray());
    internal ScenarioSequence Bind(params (string Id, Func<Task> Body)[] actions) => Bind(actions.ToDictionary(a => a.Id, a => a.Body));
    internal ScenarioSequence Bind(IReadOnlyDictionary<string, Func<Task>> actions)
    {
        var missing = Steps.Select(s => s.Id).Except(actions.Keys).ToArray();
        var extra = actions.Keys.Except(Steps.Select(s => s.Id)).ToArray();
        if (missing.Length != 0 || extra.Length != 0)
            throw new ArgumentException($"Scenario {Name} binding mismatch; missing: {string.Join(", ", missing)}; extra: {string.Join(", ", extra)}.");
        return new(this, Steps.Select(s => (s, actions[s.Id])).ToArray());
    }
}

internal sealed class ScenarioSequence(ScenarioGroup group, IReadOnlyList<(ScenarioStep Step, Func<Task> Body)> steps)
{
    internal ScenarioGroup Group => group;
    internal async Task Run(TestRun run)
    {
        foreach (var (step, body) in steps)
        {
            try
            {
                if (step.Report) await run.RunCaseAsync(step.Id, body, () => run.CaptureLogsAsync());
                else await body();
            }
            catch when (step.ContinueOnFailure)
            {
                // TestRun keeps the failed result; independent world observations continue.
            }
        }
    }
}

internal sealed class ScenarioPlan(params ScenarioGroup[] groups)
{
    internal IReadOnlyList<ScenarioGroup> Groups { get; } = Array.AsReadOnly(groups.ToArray());
    internal IEnumerable<string> CaseIds => Groups.SelectMany(g => g.CaseIds);
    internal ScenarioSequence[] Bind(params ScenarioSequence[] sequences)
    {
        var byGroup = sequences.ToDictionary(s => s.Group);
        return Groups.Select(g => byGroup.TryGetValue(g, out var sequence) ? sequence
            : throw new ArgumentException("Missing scenario group binding: " + g.Name)).ToArray();
    }
}
