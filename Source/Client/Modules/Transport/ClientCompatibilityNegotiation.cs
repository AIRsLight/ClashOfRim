namespace AIRsLight.ClashOfRim.Modules.Transport;

// Retry only an unresolved, explicit request for detail. Translated diagnostics
// are presentation data; an accepted response must never trigger a second entry.
internal static class ClientCompatibilityNegotiation
{
    internal static bool NeedsDetails(bool? accepted, bool requiresDetails, string? suppliedManifest)
        => accepted != true && requiresDetails && string.IsNullOrWhiteSpace(suppliedManifest);
}
