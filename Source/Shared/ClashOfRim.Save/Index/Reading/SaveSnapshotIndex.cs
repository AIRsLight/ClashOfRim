using System.Text.Json.Serialization;

namespace AIRsLight.ClashOfRim.Save;

public sealed record SaveSnapshotIndex(
    string SavePath,
    SaveMetaSummary Meta,
    IReadOnlyList<FactionSummary> Factions,
    IReadOnlyList<SaveIndexExtensionData> Extensions,
    IReadOnlyList<WorldObjectSummary> WorldObjects,
    IReadOnlyList<MapSummary> Maps,
    IReadOnlyList<ThingSummary> Things,
    IReadOnlyList<PawnSummary> Pawns)
{
    public int? HistoryPlayerColonistCount { get; init; }

    public int? HistoryPrisonerCount { get; init; }

    public int? HistoryWealthItems { get; init; }

    public int? HistoryWealthBuildings { get; init; }

    public int? HistoryWealthPawns { get; init; }

    public int? HistoryColonistMood { get; init; }

    public int? StoryColonistsLaunched { get; init; }

    [JsonIgnore]
    public IReadOnlyDictionary<string, ThingSummary> ThingsByGlobalKey { get; } =
        BuildFirstByGlobalKey(Things, thing => thing.GlobalKey);

    [JsonIgnore]
    public IReadOnlyDictionary<string, PawnSummary> PawnsByGlobalKey { get; } =
        BuildFirstByGlobalKey(Pawns, pawn => pawn.GlobalKey);

    private static IReadOnlyDictionary<string, T> BuildFirstByGlobalKey<T>(
        IReadOnlyList<T> items,
        Func<T, string> keySelector)
    {
        var result = new Dictionary<string, T>(items.Count, StringComparer.Ordinal);
        foreach (T item in items)
        {
            result.TryAdd(keySelector(item), item);
        }

        return result;
    }
}
