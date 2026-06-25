namespace AIRsLight.ClashOfRim.Save;

public sealed class ThingDefTrapClassificationManifest
{
    private readonly Dictionary<string, ThingDefTrapClassification> byDefName;

    public ThingDefTrapClassificationManifest(IEnumerable<ThingDefTrapClassification> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        byDefName = entries
            .GroupBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, ThingDefTrapClassification> EntriesByDefName => byDefName;

    public bool TryGetTrap(ThingSummary thing, out ThingDefTrapClassification classification)
    {
        ArgumentNullException.ThrowIfNull(thing);

        if (!string.IsNullOrWhiteSpace(thing.Def)
            && byDefName.TryGetValue(thing.Def, out ThingDefTrapClassification? found)
            && found.IsTrap)
        {
            classification = found;
            return true;
        }

        classification = default!;
        return false;
    }
}
