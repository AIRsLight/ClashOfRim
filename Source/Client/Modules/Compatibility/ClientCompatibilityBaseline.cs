using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal interface IClientCompatibilityBaselineRuntime
{
    void ApplyDlcIds(IReadOnlyList<string>? ids);
    void InvalidateManifestCache();
    void Diagnose(string message);
}

internal sealed class ClientCompatibilityBaseline
{
    private readonly IClientCompatibilityBaselineRuntime runtime;
    private string manifest = "";
    private long revision;
    internal ClientCompatibilityBaseline(IClientCompatibilityBaselineRuntime runtime) => this.runtime = runtime;
    internal string Manifest => manifest;
    internal void Apply(string? source, Func<bool>? current = null)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (current?.Invoke() == false) return;
        long accepted = ++revision;
        bool Current() => current?.Invoke() != false && accepted == revision;
        manifest = source ?? "";
        IReadOnlyList<string>? dlcs = null;
        if (!string.IsNullOrWhiteSpace(source))
        {
            try { dlcs = ClientProtocolJson.Deserialize<DlcManifest>(source!, static type => type).DlcIds; }
            catch (Exception ex) { runtime.Diagnose("Failed to parse server DLC baseline: " + ex); }
        }
        if (!Current()) return;
        runtime.ApplyDlcIds(dlcs);
        if (Current()) runtime.InvalidateManifestCache();
    }
    [DataContract]
    private sealed class DlcManifest
    {
        [DataMember(Name = "dlcIds")]
        public List<string> DlcIds { get; set; } = new();
    }
}
