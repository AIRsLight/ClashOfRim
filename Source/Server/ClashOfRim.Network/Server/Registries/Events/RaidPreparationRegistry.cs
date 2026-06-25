using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public sealed class RaidPreparationRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, RaidPreparationRecord> records = new(StringComparer.Ordinal);

    public RaidPreparationRecord Create(
        string idempotencyKey,
        string raidEventId,
        string attackerUserId,
        string attackerColonyId,
        string defenderUserId,
        string defenderColonyId,
        string defenderSnapshotId,
        string targetWorldObjectId,
        string targetMapId,
        int? targetTile,
        DateTimeOffset nowUtc,
        TimeSpan lifetime)
    {
        string preparationId = BuildPreparationId(idempotencyKey);
        DateTimeOffset expiresAtUtc = nowUtc.Add(lifetime);
        var record = new RaidPreparationRecord(
            preparationId,
            raidEventId,
            attackerUserId,
            attackerColonyId,
            defenderUserId,
            defenderColonyId,
            defenderSnapshotId,
            targetWorldObjectId,
            targetMapId,
            targetTile,
            expiresAtUtc);
        lock (gate)
        {
            PruneExpiredNoLock(nowUtc);
            records[preparationId] = record;
        }

        return record;
    }

    public bool TryAuthorizeDownload(
        string? preparationId,
        string principalUserId,
        string principalColonyId,
        string defenderUserId,
        string defenderColonyId,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(preparationId))
        {
            return false;
        }

        lock (gate)
        {
            PruneExpiredNoLock(nowUtc);
            return records.TryGetValue(preparationId!, out RaidPreparationRecord? record)
                && string.Equals(record.AttackerUserId, principalUserId, StringComparison.Ordinal)
                && string.Equals(record.AttackerColonyId, principalColonyId, StringComparison.Ordinal)
                && string.Equals(record.DefenderUserId, defenderUserId, StringComparison.Ordinal)
                && string.Equals(record.DefenderColonyId, defenderColonyId, StringComparison.Ordinal)
                && record.ExpiresAtUtc >= nowUtc;
        }
    }

    public bool TryValidateCreation(
        string? preparationId,
        string raidEventId,
        string attackerUserId,
        string attackerColonyId,
        string defenderUserId,
        string defenderColonyId,
        string defenderSnapshotId,
        string targetWorldObjectId,
        string targetMapId,
        int? targetTile,
        DateTimeOffset nowUtc,
        out string failureMessage)
    {
        failureMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(preparationId))
        {
            failureMessage = ServerLocalization.Text("RaidPreparation.Required");
            return false;
        }

        lock (gate)
        {
            PruneExpiredNoLock(nowUtc);
            if (!records.TryGetValue(preparationId!, out RaidPreparationRecord? record))
            {
                failureMessage = ServerLocalization.Text("RaidPreparation.Expired");
                return false;
            }

            if (!string.Equals(record.RaidEventId, raidEventId, StringComparison.Ordinal)
                || !string.Equals(record.AttackerUserId, attackerUserId, StringComparison.Ordinal)
                || !string.Equals(record.AttackerColonyId, attackerColonyId, StringComparison.Ordinal)
                || !string.Equals(record.DefenderUserId, defenderUserId, StringComparison.Ordinal)
                || !string.Equals(record.DefenderColonyId, defenderColonyId, StringComparison.Ordinal)
                || !string.Equals(record.DefenderSnapshotId, defenderSnapshotId, StringComparison.Ordinal)
                || !string.Equals(record.TargetWorldObjectId, targetWorldObjectId, StringComparison.Ordinal)
                || !string.Equals(record.TargetMapId, targetMapId, StringComparison.Ordinal)
                || record.TargetTile != targetTile)
            {
                failureMessage = ServerLocalization.Text("RaidPreparation.Mismatch");
                return false;
            }

            if (record.ExpiresAtUtc < nowUtc)
            {
                records.Remove(preparationId!);
                failureMessage = ServerLocalization.Text("RaidPreparation.Expired");
                return false;
            }

            return true;
        }
    }

    public int RemoveForColony(string userId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (gate)
        {
            List<string>? removed = null;
            foreach (KeyValuePair<string, RaidPreparationRecord> pair in records)
            {
                RaidPreparationRecord record = pair.Value;
                if ((string.Equals(record.AttackerUserId, userId, StringComparison.Ordinal)
                        && string.Equals(record.AttackerColonyId, colonyId, StringComparison.Ordinal))
                    || (string.Equals(record.DefenderUserId, userId, StringComparison.Ordinal)
                        && string.Equals(record.DefenderColonyId, colonyId, StringComparison.Ordinal)))
                {
                    removed ??= new List<string>();
                    removed.Add(pair.Key);
                }
            }

            if (removed is null)
            {
                return 0;
            }

            foreach (string key in removed)
            {
                records.Remove(key);
            }

            return removed.Count;
        }
    }

    private static string BuildPreparationId(string idempotencyKey)
    {
        string source = idempotencyKey ?? string.Empty;
        char[] normalized = new char[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            char ch = source[i];
            normalized[i] = char.IsLetterOrDigit(ch) ? ch : '-';
        }

        string normalizedKey = new(normalized);
        return "raid-prep:" + normalizedKey;
    }

    private void PruneExpiredNoLock(DateTimeOffset nowUtc)
    {
        List<string>? expired = null;
        foreach (KeyValuePair<string, RaidPreparationRecord> pair in records)
        {
            if (pair.Value.ExpiresAtUtc < nowUtc)
            {
                expired ??= new List<string>();
                expired.Add(pair.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (string key in expired)
        {
            records.Remove(key);
        }
    }
}

public sealed record RaidPreparationRecord(
    string PreparationId,
    string RaidEventId,
    string AttackerUserId,
    string AttackerColonyId,
    string DefenderUserId,
    string DefenderColonyId,
    string DefenderSnapshotId,
    string TargetWorldObjectId,
    string TargetMapId,
    int? TargetTile,
    DateTimeOffset ExpiresAtUtc);
