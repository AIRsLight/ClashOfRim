using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using ClashOfRim.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
            || t == typeof(RaidSettlementOperationExecutor) || t == typeof(RaidSettlementDeferredScheduler)
            || t == typeof(RaidSettlementScheduleRequest) || t == typeof(RaidEvidenceRequestAdapter)
            || t == typeof(RaidSettlementPostUploadProcessor)
            || t == typeof(AIRsLight.ClashOfRim.Network.Plugins.SnapshotPostUploadContext)
            || t == typeof(AIRsLight.ClashOfRim.Network.Plugins.SnapshotPostUploadDeferredContext)
            || t == typeof(SnapshotPostUploadPluginAdapter)
            || t == typeof(AIRsLight.ClashOfRim.Network.Plugins.SnapshotPostUploadJobExecutor)
            || t == typeof(AIRsLight.ClashOfRim.Network.Plugins.SnapshotPostUploadBackgroundService) || t == typeof(IServiceProvider));
        Require(violations.Count == 0, string.Join(Environment.NewLine, violations));
        Type[] commitModules = [typeof(SnapshotCommitApplication), typeof(SnapshotCommitJournal), typeof(SnapshotConfirmationReceipts), typeof(SnapshotCommitRecoveryPreparation)];
        var commitBackReferences = ModuleDependencyGuard.FindViolations(
            modules.Where(t => commitModules.Any(root => t == root || t.FullName!.StartsWith(root.FullName + "+", StringComparison.Ordinal))),
            t => t == typeof(SnapshotCommitService));
        Require(commitBackReferences.Count == 0, string.Join(Environment.NewLine, commitBackReferences));
        var business = modules.Where(t =>
            t.Namespace is "AIRsLight.ClashOfRim.Network.Modules.Bank" or "AIRsLight.ClashOfRim.Network.Modules.Gifts"
                or "AIRsLight.ClashOfRim.Network.Modules.Transfers" or "AIRsLight.ClashOfRim.Network.Modules.Trades" or "AIRsLight.ClashOfRim.Network.Modules.Shop" or "AIRsLight.ClashOfRim.Network.Modules.Mercenaries"
                or "AIRsLight.ClashOfRim.Network.Modules.Diplomacy" or "AIRsLight.ClashOfRim.Network.Modules.Support" or "AIRsLight.ClashOfRim.Network.Modules.Raids" or "AIRsLight.ClashOfRim.Network.Modules.Accounts"
                or "AIRsLight.ClashOfRim.Network.Modules.Compatibility" or "AIRsLight.ClashOfRim.Network.Modules.Sessions"
                or "AIRsLight.ClashOfRim.Network.Modules.World" or "AIRsLight.ClashOfRim.Network.Modules.Chat"
                or "AIRsLight.ClashOfRim.Network.Modules.Achievements" or "AIRsLight.ClashOfRim.Network.Modules.Admin"
                or "AIRsLight.ClashOfRim.Network.Modules.Events"
            && !IsHttpEndpoint(t)).ToArray();
        var transportLeaks = ModuleDependencyGuard.FindViolations(business,
            t => t.Namespace?.StartsWith("Microsoft.AspNetCore.Http", StringComparison.Ordinal) == true);
        Require(transportLeaks.Count == 0, string.Join(Environment.NewLine, transportLeaks));

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(new ClashOfRimNetworkState());
        await using var app = builder.Build();
        ClashOfRimNetworkServer.MapEndpoints(app);
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
    private static bool IsHttpEndpoint(Type type)
    {
        // Async handlers can nest their state machine beneath a closure type.
        for (Type? owner = type; owner is not null; owner = owner.DeclaringType)
            if (owner.Name is "BankEndpoints" or "GiftEndpoints" or "TradeEndpoints" or "ShopEndpoints"
                or "MercenaryEndpoints" or "SupportEndpoints" or "RaidEndpoints" or "DiplomacyEndpoints"
                or "AccountEndpoints" or "SessionEndpoints" or "WorldEndpoints" or "AdminEndpoints"
                or "ChatEndpoints" or "AchievementEndpoints" or "EventReadEndpoints" or "EventFailureEndpoints") return true;
        return false;
    }

    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    private sealed class ForbiddenProbe { public static void Execute() { } }
    private sealed class SignatureProbe { public ForbiddenProbe? Result { get; set; } }
    private sealed class CallProbe { public void Run() => ForbiddenProbe.Execute(); }
    private sealed class GenericProbe { public Func<ForbiddenProbe>? Factory { get; set; } }
    private sealed class AllowedProbe { public int Run(int number) => number + 1; }
}
