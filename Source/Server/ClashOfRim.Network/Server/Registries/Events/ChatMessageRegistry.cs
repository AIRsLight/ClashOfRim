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
    private readonly IJsonPersistenceSlot? persistence;
    private readonly List<ChatMessageRecord> messages = new();
    private long nextSequence = 1;

    public ChatMessageRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal ChatMessageRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
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
        if (persistence is null)
        {
            return;
        }

        string? json = persistence.Read();
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        ChatMessageRegistryPersistence? persisted = JsonSerializer.Deserialize<ChatMessageRegistryPersistence>(json, JsonOptions);
        if (persisted?.Messages is not null)
        {
            messages.Clear();
            messages.AddRange(persisted.Messages.OrderBy(message => message.Sequence));
        }

        nextSequence = Math.Max(
            persisted?.NextSequence ?? 1,
            messages.Count == 0 ? 1 : messages.Max(message => message.Sequence) + 1);
    }

    private void SaveLocked()
    {
        if (persistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new ChatMessageRegistryPersistence(nextSequence, messages.ToList()),
            JsonOptions);
        persistence.Write(json);
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
