using AIRsLight.ClashOfRim.Protocol;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class AdminControlRegistry
{
    private const int MaxAuditRecords = 100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly IJsonPersistenceSlot? persistence;
    private readonly HashSet<string> bannedUsers = new(StringComparer.Ordinal);
    private readonly List<AdminAuditRecord> auditRecords = new();
    private bool maintenanceLoginLocked;
    private string? maintenanceReason;

    public AdminControlRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal AdminControlRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
        Load();
    }

    public bool MaintenanceLoginLocked
    {
        get
        {
            lock (gate)
            {
                return maintenanceLoginLocked;
            }
        }
    }

    public string? MaintenanceReason
    {
        get
        {
            lock (gate)
            {
                return maintenanceReason;
            }
        }
    }

    public bool IsBanned(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (gate)
        {
            return bannedUsers.Contains(userId);
        }
    }

    public bool Ban(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (gate)
        {
            bool added = bannedUsers.Add(userId);
            if (added)
            {
                SaveLocked();
            }

            return added;
        }
    }

    public bool Unban(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (gate)
        {
            bool removed = bannedUsers.Remove(userId);
            if (removed)
            {
                SaveLocked();
            }

            return removed;
        }
    }

    public void SetMaintenanceLoginLocked(bool locked, string? reason)
    {
        lock (gate)
        {
            maintenanceLoginLocked = locked;
            maintenanceReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            SaveLocked();
        }
    }

    public AdminAuditRecord AddAudit(string actionKind, string actorUserId, string? targetUserId, string? message, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        lock (gate)
        {
            var record = new AdminAuditRecord(
                actionKind,
                actorUserId,
                string.IsNullOrWhiteSpace(targetUserId) ? null : targetUserId.Trim(),
                string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
                nowUtc);
            auditRecords.Add(record);
            if (auditRecords.Count > MaxAuditRecords)
            {
                auditRecords.RemoveRange(0, auditRecords.Count - MaxAuditRecords);
            }

            SaveLocked();
            return record;
        }
    }

    public IReadOnlyList<AdminAuditRecord> ListAudit()
    {
        lock (gate)
        {
            return auditRecords
                .OrderByDescending(record => record.CreatedAtUtc)
                .ToList();
        }
    }

    public IReadOnlyList<string> ListBannedUsers()
    {
        lock (gate)
        {
            return bannedUsers
                .OrderBy(userId => userId, StringComparer.Ordinal)
                .ToList();
        }
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

        AdminControlRegistryPersistence? persisted = JsonSerializer.Deserialize<AdminControlRegistryPersistence>(json, JsonOptions);
        if (persisted is null)
        {
            return;
        }

        maintenanceLoginLocked = persisted.MaintenanceLoginLocked;
        maintenanceReason = string.IsNullOrWhiteSpace(persisted.MaintenanceReason)
            ? null
            : persisted.MaintenanceReason!.Trim();
        bannedUsers.Clear();
        foreach (string userId in persisted.BannedUsers ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(userId))
            {
                bannedUsers.Add(userId.Trim());
            }
        }

        auditRecords.Clear();
        auditRecords.AddRange((persisted.AuditRecords ?? Array.Empty<AdminAuditRecord>())
            .OrderBy(record => record.CreatedAtUtc)
            .TakeLast(MaxAuditRecords));
    }

    private void SaveLocked()
    {
        if (persistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new AdminControlRegistryPersistence(
                maintenanceLoginLocked,
                maintenanceReason,
                bannedUsers.OrderBy(userId => userId, StringComparer.Ordinal).ToList(),
                auditRecords.ToList()),
            JsonOptions);
        persistence.Write(json);
    }
}

public sealed record AdminControlRegistryPersistence(
    bool MaintenanceLoginLocked,
    string? MaintenanceReason,
    IReadOnlyList<string>? BannedUsers,
    IReadOnlyList<AdminAuditRecord>? AuditRecords);

public sealed record AdminAuditRecord(
    string ActionKind,
    string ActorUserId,
    string? TargetUserId,
    string? Message,
    DateTimeOffset CreatedAtUtc)
{
    public AdminAuditRecordDto ToDto()
    {
        return new AdminAuditRecordDto(ActionKind, ActorUserId, TargetUserId, Message, CreatedAtUtc);
    }
}
