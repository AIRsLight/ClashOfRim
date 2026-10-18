using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Compatibility;
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
    private AdminConfigurationPublication? pending;

    internal AdminConfigurationPublication? Pending { get { lock (gate) return pending; } }

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
            if (pending is not null) throw new InvalidOperationException("Complete pending configuration publication before replacing settings.");
            Save(updatedConfiguration, userId.Trim(), nowUtc, null);
            configuration = updatedConfiguration;
            updatedByUserId = userId.Trim();
            updatedAtUtc = nowUtc;
        }
    }

    internal void Stage(AdminConfigurationPublication publication)
    {
        lock (gate)
        {
            if (pending is not null) throw new InvalidOperationException("Complete pending configuration publication before staging settings.");
            Validate(publication);
            Save(configuration, updatedByUserId, updatedAtUtc, publication);
            pending = publication;
        }
    }

    internal void Complete(AdminConfigurationPublication publication)
    {
        lock (gate)
        {
            if (!ReferenceEquals(pending, publication)) throw new InvalidOperationException("Configuration publication is no longer current.");
            Save(publication.Configuration, publication.ActorUserId, publication.CreatedAtUtc, null);
            configuration = publication.Configuration;
            updatedByUserId = publication.ActorUserId;
            updatedAtUtc = publication.CreatedAtUtc;
            pending = null;
        }
    }

    private static void Validate(AdminConfigurationPublication publication)
    {
        if (publication.Version != 1 || publication.Configuration is null || string.IsNullOrWhiteSpace(publication.ActorUserId))
            throw new InvalidDataException("Unsupported or incomplete configuration publication.");
    }

    private void Load()
    {
        if (persistence is null)
        {
            return;
        }

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
        pending = persisted?.Pending;
        if (pending is not null) Validate(pending);
    }

    private void Save(AdminConfigurationDto? candidate, string? actor, DateTimeOffset? at, AdminConfigurationPublication? work)
    {
        if (persistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new ServerConfigurationPersistence(candidate, actor, at, work),
            JsonOptions);
        persistence.Write(json);
    }

    private sealed record ServerConfigurationPersistence(
        AdminConfigurationDto? Configuration,
        string? UpdatedByUserId,
        DateTimeOffset? UpdatedAtUtc,
        AdminConfigurationPublication? Pending = null);
}

internal sealed record AdminConfigurationPublication(int Version, AdminConfigurationDto Configuration,
    CompatibilityManifest? CompatibilityBaseline, string ActorUserId, DateTimeOffset CreatedAtUtc);
