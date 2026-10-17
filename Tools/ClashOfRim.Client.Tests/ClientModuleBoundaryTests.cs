using System;
using System.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using ClashOfRim.TestSupport;

internal static class ClientModuleBoundaryTests
{
    internal static void Run()
    {
        var modules = typeof(ClashOfRimMod).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("AIRsLight.ClashOfRim.Modules.", StringComparison.Ordinal) == true).ToArray();
        var violations = ModuleDependencyGuard.FindViolations(modules, t => t == typeof(ClashOfRimMod)
            || t == typeof(ClashOfRimSettings) || t == typeof(ClashOfRimModNetworkClient) || t == typeof(IServiceProvider));
        if (modules.Length == 0 || violations.Count != 0)
            throw new InvalidOperationException("Client module boundaries: " + string.Join(Environment.NewLine, violations));
        Console.WriteLine($"Client module dependency guard checked {modules.Length} product and generated types.");
    }
}
