using System;
using System.Runtime.Serialization;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim;

[DataContract]
internal sealed class ServerNotificationPayloadSummary
{
    [DataMember(Name = "NotificationId")]
    public string? NotificationId { get; set; }

    [DataMember(Name = "Title")]
    public string? Title { get; set; }

    [DataMember(Name = "Message")]
    public string? Message { get; set; }

    [DataMember(Name = "Severity")]
    public int? Severity { get; set; }

    [DataMember(Name = "RelatedEventId")]
    public string? RelatedEventId { get; set; }

    [DataMember(Name = "RelatedEventType")]
    public ServerEventType? RelatedEventType { get; set; }

    [DataMember(Name = "RelatedUserId")]
    public string? RelatedUserId { get; set; }

    [DataMember(Name = "RelatedColonyId")]
    public string? RelatedColonyId { get; set; }

    [DataMember(Name = "RelatedAccepted")]
    public bool? RelatedAccepted { get; set; }

    public bool IsRaidTimeout =>
        !string.IsNullOrWhiteSpace(NotificationId)
        && NotificationId!.StartsWith("raid-timeout:", StringComparison.Ordinal);
}
