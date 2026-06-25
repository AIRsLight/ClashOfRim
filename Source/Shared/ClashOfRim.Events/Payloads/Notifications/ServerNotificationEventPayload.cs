namespace AIRsLight.ClashOfRim.Events;

public sealed record ServerNotificationEventPayload(
    string NotificationId,
    string Title,
    string Message,
    ServerNotificationSeverity Severity,
    bool FromAdministrator,
    string? AdministratorUserId = null,
    bool OnlineOnly = false,
    string? RelatedEventId = null,
    string? RelatedEventType = null,
    string? RelatedUserId = null,
    string? RelatedColonyId = null,
    bool? RelatedAccepted = null) : LedgerEventPayload;
