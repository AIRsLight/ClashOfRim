using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using ClashOfRim.RimTestBridge;
using ClashOfRim.TestSupport;
using RimTest.Protocol;
using RimTest.Runtime;

internal static class BridgeDomainModuleTests
{
    internal static void Run()
    {
        Registration(); Dependencies(); Admission(); Observations();
    }

    private static void Registration()
    {
        var f = new Fixture();
        using var stream = typeof(BridgeDomainModuleTests).Assembly.GetManifestResourceStream("ClashOfRim.Client.Tests.BridgeDomainCommands.json")!;
        var expected = JsonSerializer.Deserialize<Capability[]>(stream, Wire.Json)!;
        Check(f.Router.Capabilities.SequenceEqual(expected), "Domain split changed the original command names, descriptions or modes.");
        Check(f.ProviderReads == 0, "Registration read product state or required an active game.");
        Console.WriteLine($"RimTest domain registration preserved {expected.Length} command contracts.");
    }

    private static void Dependencies()
    {
        var roots = typeof(DomainCommands).Assembly.GetTypes().Where(t => t.Namespace == typeof(DomainCommands).Namespace
            && t.DeclaringType is null && t.Name.EndsWith("Commands", StringComparison.Ordinal) && t != typeof(DomainCommands)).ToArray();
        Type Owner(Type type) => type.DeclaringType is { } parent ? Owner(parent) : type;
        foreach (var root in roots)
        {
            bool Owned(Type type) => type == root || type.DeclaringType is { } parent && Owned(parent);
            var types = root.Assembly.GetTypes().Where(Owned).ToArray();
            var violations = ModuleDependencyGuard.FindViolations(types, t => t == typeof(DomainCommands)
                || t == typeof(ClashOfRimMod) || t == typeof(ClashOfRimSettings) || t == typeof(Verse.LoadedModManager) || t == typeof(IServiceProvider)
                || t == typeof(RimTest.RimWorld.GameBridge) || t == typeof(ClientActions)
                || roots.Contains(Owner(t)) && Owner(t) != root);
            Check(violations.Count == 0, root.Name + " depends on a host or sibling domain: " + string.Join(Environment.NewLine, violations));
        }
        Check(roots.Length == 18, "Missing command module in dependency verification.");
    }

    private static void Admission()
    {
        var f = new Fixture();
        foreach (string? server in new[] { null, "", "not a URI", "https://localhost:4123", "http://192.0.2.10:4123", "ftp://localhost" })
        {
            f.Address = server;
            foreach (var command in f.Router.Capabilities.Where(c => c.Mode is "semantic" or "fixture"))
            {
                var response = f.Send(command.Method, new { acknowledgeFixture = true });
                Check(response.Error?.Code == "invalid_context", command.Method + " read or changed game state before loopback admission.");
            }
        }
        f.Address = "http://localhost:4123";
        foreach (var command in f.Router.Capabilities.Where(c => c.Mode == "fixture"))
        foreach (bool acknowledge in new[] { false, true })
            Check(f.Send(command.Method, new { acknowledgeFixture = acknowledge }).Error?.Code == "disabled",
                command.Method + " bypassed debug capability admission.");
        f.Router.Register("debug.invoke", "Test capability", "fixture", _ => null);
        foreach (var command in f.Router.Capabilities.Where(c => c.Method.StartsWith("clash.fixture.", StringComparison.Ordinal)))
            Check(f.Send(command.Method, new { acknowledgeFixture = false }).Error?.Code == "disabled",
                command.Method + " bypassed explicit fixture acknowledgement.");
        foreach (string server in new[] { "http://localhost:4123", "http://127.0.0.1:4123", "http://[::1]:4123" })
        {
            f.Address = server;
            f.Policy.RequireFixture(Wire.Element(new { acknowledgeFixture = true }));
        }
        Check(f.ProviderReads == 0, "Rejected commands read unrelated product providers.");
    }

    private static void Observations()
    {
        var first = new Fixture(); var second = new Fixture();
        first.AchievementVersion = 7; second.AchievementVersion = 31;
        Check(first.Read("clash.achievement.version").GetProperty("version").GetInt32() == 7
            && second.Read("clash.achievement.version").GetProperty("version").GetInt32() == 31,
            "Achievement observation did not use its own current module provider.");
        first.AchievementVersion++;
        Check(first.Read("clash.achievement.version").GetProperty("version").GetInt32() == 8, "Achievement version was cached by the bridge.");
        first.Bank = new() { OpenDebts = new() { new() { DebtId = "first" } } };
        second.Bank = new() { OpenDebts = new() { new() { DebtId = "second" } } };
        Check(Item(first.Read("clash.bank.debts")).GetProperty("data").GetProperty("debtId").GetString() == "first"
            && Item(second.Read("clash.bank.debts")).GetProperty("data").GetProperty("debtId").GetString() == "second", "Bank modules shared observations.");
        first.Bank = null;
        Check(first.Read("clash.bank.debts").GetProperty("total").GetInt32() == 0, "Cleared bank data retained old debts.");
        first.Listings = new[] { new ModServerShopListingDto { ListingId = "listing" } };
        Check(first.Read("clash.shop.listing", new { listingId = "listing" }).GetProperty("listing").ValueKind == JsonValueKind.Object, "Shop lookup lost the selected record.");
        first.Listings = Array.Empty<ModServerShopListingDto>();
        Check(first.Read("clash.shop.listing", new { listingId = "listing" }).GetProperty("listing").ValueKind == JsonValueKind.Null, "Shop lookup retained a removed listing.");

        first.Details = new[] { new ModEventDetailDto { EventId = "event-first" } };
        second.Details = new[] { new ModEventDetailDto { EventId = "event-second" } };
        string handle = Handle(first.Read("clash.events"));
        for (int i = 0; i < 100; i++) Check(Handle(first.Read("clash.events")) == handle, "Unchanged events allocated new handles.");
        string otherHandle = Handle(second.Read("clash.events"));
        Check(handle != otherHandle && ReferenceEquals(first.Objects.Get(handle), first.Details[0]), "Event module crossed registry ownership.");
        first.Details = new[] { new ModEventDetailDto { EventId = "changed" } }; first.DetailVersion++;
        Check(Handle(first.Read("clash.events")) != handle && Handle(second.Read("clash.events")) == otherHandle,
            "Refreshing one event module reset another module's observations.");
    }

    private static JsonElement Item(JsonElement page) => page.GetProperty("items")[0];
    private static string Handle(JsonElement page) => Item(page).GetProperty("reference").GetProperty("handle").GetString()!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture
    {
        internal readonly CommandRouter Router = new();
        internal readonly ObjectRegistry Objects = new();
        internal readonly BridgeActionPolicy Policy;
        internal string? Address;
        internal int ProviderReads, AchievementVersion, DetailVersion;
        internal ModBankStatusResponseDto? Bank;
        internal IReadOnlyList<ModServerShopListingDto> Listings = Array.Empty<ModServerShopListingDto>();
        internal IReadOnlyList<ModEventDetailDto> Details = Array.Empty<ModEventDetailDto>();
        internal Fixture()
        {
            Policy = new(() => Address, Router);
            DomainCommands.Register(Router, Objects, Policy, ReadEvents, () => { ProviderReads++; return Bank; },
                () => { ProviderReads++; return Listings; }, _ => throw new InvalidOperationException("Unexpected abandonment"),
                () => { ProviderReads++; return AchievementVersion; }, () => { ProviderReads++; return "world"; });
        }
        private IReadOnlyList<ModEventDetailDto>? ReadEvents(int? known, out int version)
        { ProviderReads++; version = DetailVersion; return known == version ? null : Details; }
        internal Response Send(string method, object? args = null)
        {
            var task = Router.SubmitAsync(new Request(Guid.NewGuid().ToString(), method, Wire.Element(args ?? new { })));
            Router.Pump(); return task.GetAwaiter().GetResult();
        }
        internal JsonElement Read(string method, object? args = null)
        {
            var response = Send(method, args);
            Check(response.Error is null, method + ": " + response.Error); return response.Result;
        }
    }
}
