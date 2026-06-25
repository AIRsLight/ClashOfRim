using System.Text.Json.Serialization;

namespace AIRsLight.ClashOfRim.Events;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "payloadType")]
[JsonDerivedType(typeof(RaidEventPayload), "raid")]
[JsonDerivedType(typeof(GiftEventPayload), "gift")]
[JsonDerivedType(typeof(TradeEventPayload), "trade")]
[JsonDerivedType(typeof(SupportPawnEventPayload), "supportPawn")]
[JsonDerivedType(typeof(AllianceRequestEventPayload), "allianceRequest")]
[JsonDerivedType(typeof(AllianceCancellationEventPayload), "allianceCancellation")]
[JsonDerivedType(typeof(WarDeclarationEventPayload), "warDeclaration")]
[JsonDerivedType(typeof(PeaceRequestEventPayload), "peaceRequest")]
[JsonDerivedType(typeof(ServerNotificationEventPayload), "serverNotification")]
public abstract record LedgerEventPayload;
