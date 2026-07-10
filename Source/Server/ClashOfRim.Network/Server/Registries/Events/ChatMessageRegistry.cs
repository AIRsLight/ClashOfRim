using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ChatMessageRegistry
{
    public const string ChannelPublic = "Public";
    public const string ChannelPrivate = "Private";
    private const int MaxStoredMessages = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly IKeyedJsonRecordStore? structuredPersistence;
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly List<ChatMessageRecord> messages = new();
    private long nextSequence = 1;

    public ChatMessageRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal ChatMessageRegistry(IJsonPersistenceSlot? persistence)
        : this(null, persistence)
    {
    }

    internal ChatMessageRegistry(
        IKeyedJsonRecordStore? structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
        Load();
    }

    public ChatMessageRecord Add(
        string fromUserId,
        string fromColonyId,
        string? targetUserId,
        string text,
        DateTimeOffset sentAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromColonyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        lock (gate)
        {
            ChatMessageRecord record = new(
                nextSequence++,
                "chat-" + Guid.NewGuid().ToString("N"),
                fromUserId,
                fromColonyId,
                string.IsNullOrWhiteSpace(targetUserId) ? null : targetUserId!.Trim(),
                NormalizeText(text),
                sentAtUtc);
            messages.Add(record);
            TrimLocked();
            SaveLocked();
            return record;
        }
    }

    public IReadOnlyList<ChatMessageRecord> ListVisible(string userId, long afterSequence, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        int take = Math.Clamp(limit <= 0 ? 100 : limit, 1, 200);
        long minimumSequence = Math.Max(0, afterSequence);
        lock (gate)
        {
            if (minimumSequence <= 0)
            {
                return ListLatestVisibleLocked(userId, take);
            }

            return ListVisibleAfterLocked(userId, minimumSequence, take);
        }
    }

    private IReadOnlyList<ChatMessageRecord> ListLatestVisibleLocked(string userId, int take)
    {
        var result = new List<ChatMessageRecord>(take);
        for (int index = messages.Count - 1; index >= 0 && result.Count < take; index--)
        {
            ChatMessageRecord message = messages[index];
            if (IsVisibleTo(message, userId))
            {
                result.Add(message);
            }
        }

        result.Reverse();
        return result;
    }

    private IReadOnlyList<ChatMessageRecord> ListVisibleAfterLocked(string userId, long minimumSequence, int take)
    {
        var result = new List<ChatMessageRecord>(take);
        foreach (ChatMessageRecord message in messages)
        {
            if (message.Sequence <= minimumSequence || !IsVisibleTo(message, userId))
            {
                continue;
            }

            result.Add(message);
            if (result.Count >= take)
            {
                break;
            }
        }

        return result;
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
            TrimLocked();
            SaveLocked();
        }

        nextSequence = Math.Max(
            nextSequence,
            messages.Count == 0 ? 1 : messages.Max(message => message.Sequence) + 1);
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
                ChatMessageRecord? record = JsonSerializer.Deserialize<ChatMessageRecord>(pair.Value, JsonOptions);
                if (record is not null && record.Sequence > 0 && !string.IsNullOrWhiteSpace(record.MessageId))
                {
                    messages.Add(record);
                }
            }
            catch (JsonException)
            {
            }
        }

        if (messages.Count > 0)
        {
            messages.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        }
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

        ChatMessageRegistryPersistence? persisted = JsonSerializer.Deserialize<ChatMessageRegistryPersistence>(json, JsonOptions);
        if (persisted?.Messages is not null)
        {
            HashSet<long> existingSequences = messages.Select(message => message.Sequence).ToHashSet();
            List<ChatMessageRecord> imported = persisted.Messages
                .Where(message => message.Sequence > 0
                    && !string.IsNullOrWhiteSpace(message.MessageId)
                    && !existingSequences.Contains(message.Sequence))
                .OrderBy(message => message.Sequence)
                .ToList();
            messages.AddRange(imported);
            if (imported.Count > 0)
            {
                messages.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            }

            nextSequence = Math.Max(
                persisted.NextSequence,
                messages.Count == 0 ? 1 : messages.Max(message => message.Sequence) + 1);
            return imported.Count > 0;
        }

        return false;
    }

    private void SaveLocked()
    {
        if (structuredPersistence is not null)
        {
            structuredPersistence.ReplaceAll(messages.ToDictionary(
                message => MessageRowKey(message.Sequence),
                message => JsonSerializer.Serialize(message, JsonOptions),
                StringComparer.Ordinal));
            return;
        }

        if (legacyPersistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new ChatMessageRegistryPersistence(nextSequence, messages.ToList()),
            JsonOptions);
        legacyPersistence.Write(json);
    }

    private void TrimLocked()
    {
        if (messages.Count <= MaxStoredMessages)
        {
            return;
        }

        int removeCount = messages.Count - MaxStoredMessages;
        messages.RemoveRange(0, removeCount);
    }

    private static bool IsVisibleTo(ChatMessageRecord message, string userId)
    {
        if (string.IsNullOrWhiteSpace(message.TargetUserId))
        {
            return true;
        }

        return string.Equals(message.FromUserId, userId, StringComparison.Ordinal)
            || string.Equals(message.TargetUserId, userId, StringComparison.Ordinal);
    }

    private static string NormalizeText(string text)
    {
        return text.Trim().Length <= 500
            ? text.Trim()
            : text.Trim()[..500];
    }

    private static string MessageRowKey(long sequence)
    {
        return "message:" + sequence.ToString("D20", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record ChatMessageRegistryPersistence(
        long NextSequence,
        IReadOnlyList<ChatMessageRecord> Messages);
}

public sealed record ChatMessageRecord(
    long Sequence,
    string MessageId,
    string FromUserId,
    string FromColonyId,
    string? TargetUserId,
    string Text,
    DateTimeOffset SentAtUtc)
{
    public string Channel => string.IsNullOrWhiteSpace(TargetUserId)
        ? ChatMessageRegistry.ChannelPublic
        : ChatMessageRegistry.ChannelPrivate;
}
