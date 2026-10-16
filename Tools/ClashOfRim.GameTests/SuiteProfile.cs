namespace ClashOfRim.GameTests;

internal sealed record ClientProfile(string Language = "English", string Culture = "en-US");
internal sealed record SuiteProfile
{
    public string Id { get; init; } = "core-features";
    public bool Features { get; init; } = true;
    public bool GiftReplay { get; init; }
    public string? Stability { get; init; }
    public string[]? CatalogDefinitions { get; init; }
    public int Coverage { get; init; } = 30;
    public string Seed { get; init; } = "RimTest shared world";
    public string Storyteller { get; init; } = "Cassandra";
    public string Difficulty { get; init; } = "Peaceful";
    public string BaseDifficulty { get; init; } = "Medium";
    public Dictionary<string, float>? CustomValues { get; init; }
    public string Rainfall { get; init; } = "Normal";
    public string Temperature { get; init; } = "Normal";
    public string Population { get; init; } = "Normal";
    public string Landmarks { get; init; } = "Normal";
    public float Pollution { get; init; }
    public string[] Expansions { get; init; } = [];
    public ClientProfile A { get; init; } = new();
    public ClientProfile B { get; init; } = new();
    public void Validate()
    {
        if (Coverage is not (30 or 50 or 100) || string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Seed))
            throw new InvalidDataException("Expected named profile, seed and offered coverage.");
        if (CustomValues is not null && Difficulty != "Custom") throw new InvalidDataException("Custom values require Custom difficulty.");
        if (Features && GiftReplay) throw new InvalidDataException("Gift replay uses an isolated destructive-boundary scenario.");
        if (Stability is not null && (Features || GiftReplay || Stability is not ("transfers" or "catalog" or "catalog-sample" or "stress" or "items" or "permanent" or "death")))
            throw new InvalidDataException("Stability requires an isolated transfers/catalog/stress profile.");
        if ((Stability == "catalog-sample") != (CatalogDefinitions is { Length: > 0 }))
            throw new InvalidDataException("Only an explicitly sampled catalog profile may select definitions.");
        foreach (var c in new[] { A, B }) _ = System.Globalization.CultureInfo.GetCultureInfo(c.Culture);
    }
}
