namespace AIRsLight.ClashOfRim.Compatibility;

public sealed record DefSummary
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public int Hash { get; init; }
}
