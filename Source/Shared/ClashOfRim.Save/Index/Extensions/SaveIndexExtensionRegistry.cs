namespace AIRsLight.ClashOfRim.Save;

public static class SaveIndexExtensionRegistry
{
    private static readonly object SyncRoot = new();
    private static readonly List<ISaveIndexExtension> Extensions = new();

    public static IReadOnlyList<ISaveIndexExtension> Registered
    {
        get
        {
            lock (SyncRoot)
            {
                return Extensions.ToList();
            }
        }
    }

    public static void Register(ISaveIndexExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);

        lock (SyncRoot)
        {
            if (!Extensions.Any(existing => existing.GetType() == extension.GetType()))
            {
                Extensions.Add(extension);
            }
        }
    }
}
