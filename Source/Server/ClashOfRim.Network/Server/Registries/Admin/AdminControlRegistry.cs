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
    private readonly IKeyedJsonRecordStore? structuredPersistence;
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly HashSet<string> bannedUsers = new(StringComparer.Ordinal);
    private readonly List<AdminAuditRecord> auditRecords = new();
    private bool maintenanceLoginLocked;
    private string? maintenanceReason;

    public AdminControlRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal AdminControlRegistry(IJsonPersistenceSlot? persistence)
        : this(null, persistence)
    {
    }

    internal AdminControlRegistry(
        IKeyedJsonRecordStore? structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
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
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        bool importedLegacy = !hasStructured
            && (structuredPersistence is null || LegacyStructuredImportScope.IsActive)
            && LoadLegacyReadOnly();
        if (importedLegacy && structuredPersistence is not null)
        {
            SaveLocked();
        }
    }

    private void LoadStructured()
    {
        if (structuredPersistence is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> pair in structuredPersistence.ReadAll())
        {
            try
            {
                if (string.Equals(pair.Key, "state:maintenance", StringComparison.Ordinal))
                {
                    AdminControlStateRecord? state =
                        JsonSerializer.Deserialize<AdminControlStateRecord>(pair.Value, JsonOptions);
                    maintenanceLoginLocked = state?.MaintenanceLoginLocked ?? false;
                    maintenanceReason = string.IsNullOrWhiteSpace(state?.MaintenanceReason)
                        ? null
                        : state!.MaintenanceReason!.Trim();
                }
                else if (pair.Key.StartsWith("ban:", StringComparison.Ordinal))
                {
                    string? userId = JsonSerializer.Deserialize<string>(pair.Value, JsonOptions);
                    if (!string.IsNullOrWhiteSpace(userId))
                    {
                        bannedUsers.Add(userId.Trim());
                    }
                }
                else if (pair.Key.StartsWith("audit:", StringComparison.Ordinal))
                {
                    AdminAuditRecord? record = JsonSerializer.Deserialize<AdminAuditRecord>(pair.Value, JsonOptions);
                    if (record is not null)
                    {
                        auditRecords.Add(record);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        TrimAuditRecords();
    }

    private bool LoadLegacyReadOnly()
    {
        if (legacyPersistence is null)
        {
            return false;
        }

        string? json = legacyPersistence.Read();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        AdminControlRegistryPersistence? persisted = JsonSerializer.Deserialize<AdminControlRegistryPersistence>(json, JsonOptions);
        if (persisted is null)
        {
            return false;
        }

        bool imported = false;
        if (!maintenanceLoginLocked && persisted.MaintenanceLoginLocked)
        {
            maintenanceLoginLocked = true;
            maintenanceReason = string.IsNullOrWhiteSpace(persisted.MaintenanceReason)
                ? null
                : persisted.MaintenanceReason!.Trim();
            imported = true;
        }

        foreach (string userId in persisted.BannedUsers ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(userId))
            {
                imported |= bannedUsers.Add(userId.Trim());
            }
        }

        HashSet<string> existingAuditKeys = auditRecords.Select(AuditRecordKey).ToHashSet(StringComparer.Ordinal);
        foreach (AdminAuditRecord record in persisted.AuditRecords ?? Array.Empty<AdminAuditRecord>())
        {
            string key = AuditRecordKey(record);
            if (existingAuditKeys.Add(key))
            {
                auditRecords.Add(record);
                imported = true;
            }
        }

        TrimAuditRecords();
        return imported;
    }

    private void SaveLocked()
    {
        if (structuredPersistence is not null)
        {
            Dictionary<string, string> rows = new(StringComparer.Ordinal)
            {
                ["state:maintenance"] = JsonSerializer.Serialize(
                    new AdminControlStateRecord(maintenanceLoginLocked, maintenanceReason),
                    JsonOptions)
            };
            foreach (string userId in bannedUsers.OrderBy(userId => userId, StringComparer.Ordinal))
            {
                rows[BanRowKey(userId)] = JsonSerializer.Serialize(userId, JsonOptions);
            }

            for (int index = 0; index < auditRecords.Count; index++)
            {
                AdminAuditRecord record = auditRecords[index];
                rows[AuditRowKey(record, index)] = JsonSerializer.Serialize(record, JsonOptions);
            }

            structuredPersistence.ReplaceAll(rows);
            return;
        }

        if (legacyPersistence is null)
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
        legacyPersistence.Write(json);
    }

    private void TrimAuditRecords()
    {
        auditRecords.Sort((left, right) => left.CreatedAtUtc.CompareTo(right.CreatedAtUtc));
        if (auditRecords.Count > MaxAuditRecords)
        {
            auditRecords.RemoveRange(0, auditRecords.Count - MaxAuditRecords);
        }
    }

    private static string BanRowKey(string userId)
    {
        return "ban:" + userId;
    }

    private static string AuditRowKey(AdminAuditRecord record, int index)
    {
        return "audit:" + record.CreatedAtUtc.ToUnixTimeMilliseconds().ToString("D20", System.Globalization.CultureInfo.InvariantCulture)
            + ":" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string AuditRecordKey(AdminAuditRecord record)
    {
        return record.ActionKind + "\n"
            + record.ActorUserId + "\n"
            + (record.TargetUserId ?? string.Empty) + "\n"
            + (record.Message ?? string.Empty) + "\n"
            + record.CreatedAtUtc.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record AdminControlStateRecord(
        bool MaintenanceLoginLocked,
        string? MaintenanceReason);
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
