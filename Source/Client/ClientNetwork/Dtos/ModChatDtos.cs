using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModSendChatMessageRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "targetUserId")]
    public string? TargetUserId { get; set; }

    [DataMember(Name = "text")]
    public string Text { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModListChatMessagesRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "afterSequence")]
    public long AfterSequence { get; set; }

    [DataMember(Name = "limit")]
    public int Limit { get; set; }
}

[DataContract]
public sealed class ModChatMessageDto
{
    [DataMember(Name = "sequence")]
    public long Sequence { get; set; }

    [DataMember(Name = "messageId")]
    public string MessageId { get; set; } = string.Empty;

    [DataMember(Name = "channel")]
    public string Channel { get; set; } = string.Empty;

    [DataMember(Name = "fromUserId")]
    public string FromUserId { get; set; } = string.Empty;

    [DataMember(Name = "fromColonyId")]
    public string FromColonyId { get; set; } = string.Empty;

    [DataMember(Name = "targetUserId")]
    public string? TargetUserId { get; set; }

    [DataMember(Name = "text")]
    public string Text { get; set; } = string.Empty;

    [DataMember(Name = "sentAtUtc")]
    public string? SentAtUtc { get; set; }
}

[DataContract]
public sealed class ModSendChatMessageResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "message")]
    public ModChatMessageDto? Message { get; set; }
}

[DataContract]
public sealed class ModListChatMessagesResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "messages")]
    public List<ModChatMessageDto> Messages { get; set; } = new();

    [DataMember(Name = "latestSequence")]
    public long LatestSequence { get; set; }
}
