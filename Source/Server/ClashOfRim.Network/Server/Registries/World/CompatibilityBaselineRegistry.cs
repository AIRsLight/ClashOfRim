using AIRsLight.ClashOfRim.Compatibility;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIRsLight.ClashOfRim.Network;

public sealed class CompatibilityBaselineRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object gate = new();
    private readonly IJsonPersistenceSlot? persistence;
    private CompatibilityManifest? baseline;
    private DateTimeOffset? updatedAtUtc;
    private string? updatedByUserId;

    public CompatibilityBaselineRegistry(string? persistencePath = null)
        : this(string.IsNullOrWhiteSpace(persistencePath) ? null : new FileJsonPersistenceSlot(persistencePath))
    {
    }

    internal CompatibilityBaselineRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
        Load();
    }

    public CompatibilityManifest? Current
    {
        get
        {
            lock (gate)
            {
                return baseline;
            }
        }
    }

    public CompatibilityBaselineUpdateResult EnsureBaseline(
        CompatibilityManifest manifest,
        string userId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (gate)
        {
            if (baseline is not null)
            {
                return new CompatibilityBaselineUpdateResult(false, baseline, updatedByUserId, updatedAtUtc);
            }

            baseline = manifest;
            updatedByUserId = userId;
            updatedAtUtc = nowUtc;
            SaveLocked();
            return new CompatibilityBaselineUpdateResult(true, baseline, updatedByUserId, updatedAtUtc);
        }
    }

    public CompatibilityBaselineUpdateResult ReplaceBaseline(
        CompatibilityManifest manifest,
        string userId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (gate)
        {
            baseline = manifest;
            updatedByUserId = userId;
            updatedAtUtc = nowUtc;
            SaveLocked();
            return new CompatibilityBaselineUpdateResult(true, baseline, updatedByUserId, updatedAtUtc);
        }
    }

    private void Load()
    {
        if (persistence is null)
        {
            return;
        }

        try
        {
            string? json = persistence.Read();
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            CompatibilityBaselinePersistence? persisted =
                JsonSerializer.Deserialize<CompatibilityBaselinePersistence>(json, JsonOptions);
            baseline = persisted?.Baseline;
            updatedByUserId = persisted?.UpdatedByUserId;
            updatedAtUtc = persisted?.UpdatedAtUtc;
        }
        catch (JsonException)
        {
            baseline = null;
            updatedByUserId = null;
            updatedAtUtc = null;
        }
        catch (IOException)
        {
            baseline = null;
            updatedByUserId = null;
            updatedAtUtc = null;
        }
    }

    private void SaveLocked()
    {
        if (persistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new CompatibilityBaselinePersistence(baseline, updatedByUserId, updatedAtUtc),
            JsonOptions);
        persistence.Write(json);
    }

    private sealed record CompatibilityBaselinePersistence(
        CompatibilityManifest? Baseline,
        string? UpdatedByUserId,
        DateTimeOffset? UpdatedAtUtc);
}

public sealed record CompatibilityBaselineUpdateResult(
    bool Updated,
    CompatibilityManifest? Baseline,
    string? UpdatedByUserId,
    DateTimeOffset? UpdatedAtUtc);
