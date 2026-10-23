using static AIRsLight.ClashOfRim.Compatibility.CompatibilityManifestRules;

namespace AIRsLight.ClashOfRim.Compatibility;

// Presence and relative load order use one policy in full and summary comparisons.
// Collect matching pairs without inspecting details until list/order checks pass.
internal static class CompatibilityModSetComparison
{
    internal static IReadOnlyList<(ModManifestEntry Server, TClient Client)> Compare<TClient>(List<CompatibilityIssue> issues, IReadOnlyList<ModManifestEntry> server,
        IReadOnlyList<TClient> client, Func<TClient, bool> allowExtra,
        Action<TClient> extra) where TClient : ICompatibilityModIdentity
    {
        var byServer = server.ToDictionary(mod => NormalizeId(mod.PackageId));
        var byClient = client.ToDictionary(mod => NormalizeId(mod.PackageId));
        var matches = new List<(ModManifestEntry, TClient)>();
        foreach (var mod in server.OrderBy(mod => mod.LoadOrder))
        {
            if (byClient.TryGetValue(NormalizeId(mod.PackageId), out var candidate)) { matches.Add((mod, candidate)); continue; }
            if (mod.Role is ModCompatibilityRole.Optional or ModCompatibilityRole.OptionalPureTranslation)
                issues.Add(new(CompatibilityIssueSeverity.Info, CompatibilityIssueCode.MissingMod,
                    $"Optional mod not installed: {mod.PackageId}", mod.PackageId));
            else issues.Add(Error(CompatibilityIssueCode.MissingMod, $"Missing required mod: {mod.PackageId}", mod.PackageId));
        }
        foreach (var mod in client)
        {
            if (byServer.ContainsKey(NormalizeId(mod.PackageId))) continue;
            if (allowExtra(mod)) extra(mod);
            else issues.Add(Error(CompatibilityIssueCode.UnexpectedMod, $"Unexpected unapproved mod: {mod.PackageId}", mod.PackageId));
        }
        var serverOrder = server.Where(mod => byClient.ContainsKey(NormalizeId(mod.PackageId)) || mod.Role == ModCompatibilityRole.Required)
            .OrderBy(mod => mod.LoadOrder).Select(mod => NormalizeId(mod.PackageId));
        var clientOrder = client.Where(mod => byServer.ContainsKey(NormalizeId(mod.PackageId)) || !allowExtra(mod))
            .OrderBy(mod => mod.LoadOrder).Select(mod => NormalizeId(mod.PackageId));
        if (!serverOrder.SequenceEqual(clientOrder))
            issues.Add(Error(CompatibilityIssueCode.ModOrderMismatch,
                "Mod load order mismatch, or an unapproved non-translation mod is present", "mods"));
        return matches;
    }
}
