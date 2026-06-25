using AIRsLight.ClashOfRim.Protocol;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ServerConfigurationRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly IJsonPersistenceSlot? persistence;
    private AdminConfigurationDto? configuration;
    private string? updatedByUserId;
    private DateTimeOffset? updatedAtUtc;

    public ServerConfigurationRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal ServerConfigurationRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
        Load();
    }

    public AdminConfigurationDto? Current
    {
        get
        {
            lock (gate)
            {
                return configuration;
            }
        }
    }

    public DateTimeOffset? UpdatedAtUtc
    {
        get
        {
            lock (gate)
            {
                return updatedAtUtc;
            }
        }
    }

    public string? UpdatedByUserId
    {
        get
        {
            lock (gate)
            {
                return updatedByUserId;
            }
        }
    }

    public void Replace(AdminConfigurationDto updatedConfiguration, string userId, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(updatedConfiguration);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (gate)
        {
            configuration = updatedConfiguration;
            updatedByUserId = userId.Trim();
            updatedAtUtc = nowUtc;
            SaveLocked();
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

            ServerConfigurationPersistence? persisted =
                JsonSerializer.Deserialize<ServerConfigurationPersistence>(json, JsonOptions);
            configuration = persisted?.Configuration;
            updatedByUserId = string.IsNullOrWhiteSpace(persisted?.UpdatedByUserId)
                ? null
                : persisted!.UpdatedByUserId!.Trim();
            updatedAtUtc = persisted?.UpdatedAtUtc;
        }
        catch (JsonException)
        {
            configuration = null;
            updatedByUserId = null;
            updatedAtUtc = null;
        }
        catch (IOException)
        {
            configuration = null;
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
            new ServerConfigurationPersistence(configuration, updatedByUserId, updatedAtUtc),
            JsonOptions);
        persistence.Write(json);
    }

    private sealed record ServerConfigurationPersistence(
        AdminConfigurationDto? Configuration,
        string? UpdatedByUserId,
        DateTimeOffset? UpdatedAtUtc);
}
