namespace AIRsLight.ClashOfRim.Network;

internal static class RegistryPersistenceDiagnostics
{
    public static void ReportInvalidRecord(string collection, string rowKey, Exception exception)
    {
        Console.Error.WriteLine(
            "[ClashOfRim][Persistence][Error] Invalid structured record isolated: collection="
            + Sanitize(collection)
            + " row="
            + Sanitize(rowKey)
            + " error="
            + exception.GetType().Name
            + " message="
            + Sanitize(exception.Message));
    }

    private static string Sanitize(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }
}
