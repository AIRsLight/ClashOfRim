namespace AIRsLight.ClashOfRim.Save;

public sealed record SaveIndexExtensionData(
    string ProviderId,
    string Kind,
    string Version,
    string PayloadJson,
    IReadOnlyDictionary<string, string?> Metadata);
