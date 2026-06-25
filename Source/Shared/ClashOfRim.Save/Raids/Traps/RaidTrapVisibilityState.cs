namespace AIRsLight.ClashOfRim.Save;

public sealed class RaidTrapVisibilityState
{
    private readonly Dictionary<string, RaidHiddenTrap> hiddenByGlobalKey;
    private readonly HashSet<string> revealed = new(StringComparer.Ordinal);

    private RaidTrapVisibilityState(IEnumerable<RaidHiddenTrap> hiddenTraps)
    {
        hiddenByGlobalKey = hiddenTraps
            .GroupBy(trap => trap.Thing.GlobalKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    public IReadOnlyList<RaidHiddenTrap> HiddenTraps => hiddenByGlobalKey.Values
        .OrderBy(trap => trap.Thing.MapUniqueId, StringComparer.Ordinal)
        .ThenBy(trap => trap.Thing.LocalId, StringComparer.Ordinal)
        .ToList();

    public IReadOnlyCollection<string> RevealedGlobalKeys => revealed;

    public static RaidTrapVisibilityState FromSnapshot(
        SaveSnapshotIndex snapshot,
        ThingDefTrapClassificationManifest manifest,
        string? mapUniqueId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(manifest);

        IEnumerable<ThingSummary> things = snapshot.Things;
        if (!string.IsNullOrWhiteSpace(mapUniqueId))
        {
            things = things.Where(thing => string.Equals(thing.MapUniqueId, mapUniqueId, StringComparison.Ordinal));
        }

        IEnumerable<RaidHiddenTrap> hiddenTraps = things
            .Where(thing => !thing.IsPawn)
            .Select(thing => manifest.TryGetTrap(thing, out ThingDefTrapClassification classification)
                ? new RaidHiddenTrap(thing, classification)
                : null)
            .Where(trap => trap is not null)
            .Cast<RaidHiddenTrap>();

        return new RaidTrapVisibilityState(hiddenTraps);
    }

    public bool ContainsTrap(string globalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalKey);
        return hiddenByGlobalKey.ContainsKey(globalKey);
    }

    public bool IsRevealed(string globalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalKey);
        return revealed.Contains(globalKey);
    }

    public bool ShouldHide(ThingSummary thing, RaidTrapVisibilitySurface surface)
    {
        ArgumentNullException.ThrowIfNull(thing);
        return ShouldHide(thing.GlobalKey, surface);
    }

    public bool ShouldHide(string globalKey, RaidTrapVisibilitySurface surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalKey);
        return hiddenByGlobalKey.ContainsKey(globalKey) && !revealed.Contains(globalKey);
    }

    public RaidTrapRevealResult Reveal(string globalKey, RaidTrapRevealReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalKey);

        if (!hiddenByGlobalKey.ContainsKey(globalKey))
        {
            return new RaidTrapRevealResult(globalKey, Revealed: false, reason, RequiresMapMeshRefresh: false);
        }

        bool changed = revealed.Add(globalKey);
        return new RaidTrapRevealResult(globalKey, changed, reason, RequiresMapMeshRefresh: changed);
    }

    public IReadOnlyList<ThingSummary> GetRetainedThings(SaveSnapshotIndex snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Things;
    }
}
