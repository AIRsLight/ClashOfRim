using System;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

// Owns the parsed baseline cache and config rules. Engine mod lookup, dialogs
// and disk profile activation remain native adapters.
internal sealed class ClientModSettingsPolicy
{
    private readonly ClientCompatibilityBaseline? baseline;
    private readonly Func<string?> activeManifest;
    private readonly Action<string> diagnose;
    private string? cachedJson;
    private ClientModConfigManifest? cached;
    internal ClientModSettingsPolicy(ClientCompatibilityBaseline? baseline, Func<string?> activeManifest, Action<string> diagnose)
    { this.baseline = baseline; this.activeManifest = activeManifest; this.diagnose = diagnose; }
    internal void Invalidate() { cachedJson = null; cached = null; }
    internal bool IsControlled(string packageId, string fileName)
    {
        string? json = baseline?.Manifest;
        if (string.IsNullOrWhiteSpace(json)) json = activeManifest();
        if (string.IsNullOrWhiteSpace(json)) { Invalidate(); return false; }
        if (!string.Equals(cachedJson, json, StringComparison.Ordinal) || cached is null)
        {
            try { cached = ClientModConfigManifest.Read(json); cachedJson = json; }
            catch (Exception ex) { Invalidate(); diagnose(ex.Message); return false; }
        }
        return cached?.Enforces(packageId, fileName) == true;
    }
}
