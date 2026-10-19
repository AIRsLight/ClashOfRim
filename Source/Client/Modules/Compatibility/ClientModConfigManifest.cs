using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

// The settings lock and disk profile use the same manifest contract and ordinal
// config identity. Profile membership does not imply that editing is enforced.
[DataContract]
internal sealed class ClientModConfigManifest
{
    [DataMember(Name = "manifestId")] public string? Id { get; private set; }
    [DataMember(Name = "mods")] private List<Mod?>? Mods { get; set; }
    [DataMember(Name = "modConfigRules")] private List<Rule?>? Rules { get; set; }
    internal static ClientModConfigManifest? Read(string? json) => string.IsNullOrWhiteSpace(json) ? null
        : ClientProtocolJson.Deserialize<ClientModConfigManifest>(json!, static name => "Invalid " + name);
    internal bool Declares(string packageId, string fileName) => Mods?.Any(mod => mod is not null
        && string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
        && mod.Configs?.Any(config => config is not null && string.Equals(config.FileName, fileName, StringComparison.OrdinalIgnoreCase)) == true) == true;
    internal bool Enforces(string packageId, string fileName)
    {
        if (!Declares(packageId, fileName)) return false;
        Rule? best = null;
        foreach (var rule in Rules ?? Enumerable.Empty<Rule?>())
        {
            if (rule is null || !string.Equals(rule.PackageId?.Trim(), packageId.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(rule.FileName) && !string.Equals(rule.FileName, fileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || !string.IsNullOrWhiteSpace(rule.FileName) && string.IsNullOrWhiteSpace(best.FileName)) best = rule;
        }
        return string.IsNullOrWhiteSpace(best?.Mode) || string.Equals(best!.Mode, "Enforce", StringComparison.OrdinalIgnoreCase);
    }
    [DataContract]
    private sealed class Rule
    {
        [DataMember(Name = "packageId")] public string? PackageId { get; set; }
        [DataMember(Name = "fileName")] public string? FileName { get; set; }
        [DataMember(Name = "mode")] public string? Mode { get; set; }
    }
    [DataContract]
    private sealed class Mod
    {
        [DataMember(Name = "packageId")] public string? PackageId { get; set; }
        [DataMember(Name = "configs")] public List<Config?>? Configs { get; set; }
    }
    [DataContract]
    private sealed class Config
    {
        [DataMember(Name = "fileName")] public string? FileName { get; set; }
    }
}
