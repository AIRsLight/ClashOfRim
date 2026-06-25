using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class SendChatMessageRequest
{
    public SendChatMessageRequest(
        string userId,
        string colonyId,
        string? authToken,
        string? targetUserId,
        string text)
    {
        UserId = userId;
        ColonyId = colonyId;
        AuthToken = authToken;
        TargetUserId = targetUserId;
        Text = text;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? AuthToken { get; }

    public string? TargetUserId { get; }

    public string Text { get; }
}

public sealed class ListChatMessagesRequest
{
    public ListChatMessagesRequest(
        string userId,
        string colonyId,
        string? authToken,
        long afterSequence,
        int limit)
    {
        UserId = userId;
        ColonyId = colonyId;
        AuthToken = authToken;
        AfterSequence = afterSequence;
        Limit = limit;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? AuthToken { get; }

    public long AfterSequence { get; }

    public int Limit { get; }
}

public sealed class ChatMessageDto
{
    public ChatMessageDto(
        long sequence,
        string messageId,
        string channel,
        string fromUserId,
        string fromColonyId,
        string? targetUserId,
        string text,
        DateTimeOffset sentAtUtc)
    {
        Sequence = sequence;
        MessageId = messageId;
        Channel = channel;
        FromUserId = fromUserId;
        FromColonyId = fromColonyId;
        TargetUserId = targetUserId;
        Text = text;
        SentAtUtc = sentAtUtc;
    }

    public long Sequence { get; }

    public string MessageId { get; }

    public string Channel { get; }

    public string FromUserId { get; }

    public string FromColonyId { get; }

    public string? TargetUserId { get; }

    public string Text { get; }

    public DateTimeOffset SentAtUtc { get; }
}

public sealed class SendChatMessageResponse
{
    public SendChatMessageResponse(ProtocolResponse result, ChatMessageDto? message)
    {
        Result = result;
        Message = message;
    }

    public ProtocolResponse Result { get; }

    public ChatMessageDto? Message { get; }
}

public sealed class ListChatMessagesResponse
{
    public ListChatMessagesResponse(
        ProtocolResponse result,
        IReadOnlyList<ChatMessageDto> messages,
        long latestSequence)
    {
        Result = result;
        Messages = messages;
        LatestSequence = latestSequence;
    }

    public ProtocolResponse Result { get; }

    public IReadOnlyList<ChatMessageDto> Messages { get; }

    public long LatestSequence { get; }
}
