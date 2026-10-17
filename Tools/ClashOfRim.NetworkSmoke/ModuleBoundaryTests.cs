using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using ClashOfRim.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

internal static class ModuleBoundaryTests
{
    public static async Task RunAsync()
    {
        var bad = ModuleDependencyGuard.FindViolations(
            new[] { typeof(SignatureProbe), typeof(CallProbe), typeof(GenericProbe) }, t => t == typeof(ForbiddenProbe));
        foreach (Type probe in new[] { typeof(SignatureProbe), typeof(CallProbe), typeof(GenericProbe) })
            Require(bad.Any(f => f.StartsWith(probe.FullName!, StringComparison.Ordinal)), "Dependency guard missed " + probe.Name);
        Require(ModuleDependencyGuard.FindViolations(new[] { typeof(AllowedProbe) }, t => t == typeof(ForbiddenProbe)).Count == 0,
            "Dependency guard rejected an independent type.");

        Type[] modules = typeof(ClashOfRimNetworkState).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("AIRsLight.ClashOfRim.Network.Modules.", StringComparison.Ordinal) == true).ToArray();
        var violations = ModuleDependencyGuard.FindViolations(modules, t =>
            t == typeof(ClashOfRimNetworkServer) || t == typeof(ClashOfRimNetworkState)
            || t == typeof(LegacySnapshotRecoveryAdapter) || t == typeof(IServiceProvider));
        Require(violations.Count == 0, string.Join(Environment.NewLine, violations));

        await using var app = ClashOfRimNetworkServer.Build([], new ClashOfRimNetworkState());
        var expected = JsonSerializer.Deserialize<EndpointContract[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "ModuleEndpointContract.json")))!;
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
        foreach (var contract in expected)
        {
            var kind = Enum.Parse<ProtocolMessageKind>(contract.Kind);
            Require(ProtocolContractManifest.Find(kind).Route == contract.Route, "Protocol route changed: " + contract.Kind);
            var matches = endpoints.Where(e => e.RoutePattern.RawText == contract.Route).ToArray();
            Require(matches.Length == 1, "Route missing or registered more than once: " + contract.Route);
            Require(matches[0].Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.SequenceEqual(new[] { contract.Method }),
                "HTTP method changed: " + contract.Route);
            Require((matches[0].Metadata.GetMetadata<EndpointSnapshotRecovery>() is not null) == contract.OwnsRecovery,
                "Recovery ownership changed: " + contract.Route);
        }
        var kinds = expected.Select(x => Enum.Parse<ProtocolMessageKind>(x.Kind)).ToHashSet();
        Require(ProtocolContractManifest.Endpoints.Where(e => e.Kind != ProtocolMessageKind.ServerNotification).All(e => kinds.Contains(e.Kind)),
            "New protocol endpoint needs an explicit migration contract.");
        Console.WriteLine($"PASS: module dependency guard ({modules.Length} types), {expected.Length} routes and recovery ownership");
    }

    private sealed record EndpointContract(string Kind, string Route, string Method, bool OwnsRecovery);
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    private sealed class ForbiddenProbe { public static void Execute() { } }
    private sealed class SignatureProbe { public ForbiddenProbe? Result { get; set; } }
    private sealed class CallProbe { public void Run() => ForbiddenProbe.Execute(); }
    private sealed class GenericProbe { public Func<ForbiddenProbe>? Factory { get; set; } }
    private sealed class AllowedProbe { public int Run(int number) => number + 1; }
}
