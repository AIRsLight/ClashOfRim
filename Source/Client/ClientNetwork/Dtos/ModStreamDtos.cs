using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

public sealed class ModSessionStreamEvent
{
    public ModSessionStreamEvent(string eventName, string data)
    {
        EventName = eventName;
        Data = data;
    }

    public string EventName { get; }

    public string Data { get; }
}

[DataContract]
public sealed class ModSessionWebSocketEnvelopeDto
{
    [DataMember(Name = "eventName")]
    public string EventName { get; set; } = string.Empty;

    [DataMember(Name = "data")]
    public string Data { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModPlayerOnlineStreamEventDto
{
    [DataMember(Name = "UserId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "OnlineUserId")]
    public string OnlineUserId { get; set; } = string.Empty;

    [DataMember(Name = "PlayerOnlineVersion")]
    public long PlayerOnlineVersion { get; set; }

    [DataMember(Name = "Message")]
    public string? Message { get; set; }
}

public sealed class ModSessionStreamClosedDto
{
    public ModSessionStreamClosedDto(bool cancelled)
    {
        Cancelled = cancelled;
    }

    public bool Cancelled { get; }
}
