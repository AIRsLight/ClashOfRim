using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed partial class ModThingReferenceDto
{
    [DataMember(Name = "globalKey")]
    public string GlobalKey { get; set; } = string.Empty;

    [DataMember(Name = "defName")]
    public string? DefName { get; set; }

    [DataMember(Name = "stackCount")]
    public int StackCount { get; set; }

    [DataMember(Name = "quality")]
    public string? Quality { get; set; }

    [DataMember(Name = "hitPoints")]
    public int? HitPoints { get; set; }

    [DataMember(Name = "stuffDefName")]
    public string? StuffDefName { get; set; }

    [DataMember(Name = "maxHitPoints")]
    public int? MaxHitPoints { get; set; }

    [DataMember(Name = "minifiedInnerDefName")]
    public string? MinifiedInnerDefName { get; set; }

    [DataMember(Name = "minifiedInnerStuffDefName")]
    public string? MinifiedInnerStuffDefName { get; set; }

    [DataMember(Name = "minifiedInnerQuality")]
    public string? MinifiedInnerQuality { get; set; }

    [DataMember(Name = "minifiedInnerHitPoints")]
    public int? MinifiedInnerHitPoints { get; set; }

    [DataMember(Name = "minifiedInnerMaxHitPoints")]
    public int? MinifiedInnerMaxHitPoints { get; set; }

    [DataMember(Name = "wornByCorpse")]
    public bool? WornByCorpse { get; set; }

    [DataMember(Name = "biocoded")]
    public bool? Biocoded { get; set; }

    [DataMember(Name = "biocodedPawnLabel")]
    public string? BiocodedPawnLabel { get; set; }

    [DataMember(Name = "biocodedPawnGlobalId")]
    public string? BiocodedPawnGlobalId { get; set; }

    [DataMember(Name = "displayLabel")]
    public string? DisplayLabel { get; set; }

    [DataMember(Name = "marketValue")]
    public float? MarketValue { get; set; }

    [DataMember(Name = "uniqueWeapon")]
    public bool? UniqueWeapon { get; set; }

    [DataMember(Name = "uniqueWeaponTraits")]
    public List<string> UniqueWeaponTraits { get; set; } = new();

    [DataMember(Name = "metadata")]
    public Dictionary<string, string?> Metadata { get; set; } = new();

    [DataMember(Name = "pawnPackage")]
    public ModPawnExchangePackageDto? PawnPackage { get; set; }

    [DataMember(Name = "pawnPackageId")]
    public string? PawnPackageId { get; set; }

    [DataMember(Name = "thingPackage")]
    public ModThingStatePackageDto? ThingPackage { get; set; }

    [DataMember(Name = "thingPackageId")]
    public string? ThingPackageId { get; set; }
}

[DataContract]
public sealed class ModThingStatePackageDto
{
    [DataMember(Name = "packageVersion")]
    public int PackageVersion { get; set; } = 1;

    [DataMember(Name = "globalKey")]
    public string GlobalKey { get; set; } = string.Empty;

    [DataMember(Name = "defName")]
    public string? DefName { get; set; }

    [DataMember(Name = "label")]
    public string? Label { get; set; }

    [DataMember(Name = "stackCount")]
    public int StackCount { get; set; }

    [DataMember(Name = "scribe")]
    public ModThingScribePayloadDto? Scribe { get; set; }

    [DataMember(Name = "fingerprint")]
    public string? Fingerprint { get; set; }
}

[DataContract]
public sealed class ModThingScribePayloadDto
{
    [DataMember(Name = "xmlGzipBase64")]
    public string XmlGzipBase64 { get; set; } = string.Empty;

    [DataMember(Name = "xmlSha256")]
    public string? XmlSha256 { get; set; }

    [DataMember(Name = "uncompressedBytes")]
    public int UncompressedBytes { get; set; }
}

[DataContract]
public sealed class ModCreateTradeOrderRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "owner")]
    public ModProtocolIdentityDto? Owner { get; set; }

    [DataMember(Name = "offeredThings")]
    public List<ModThingReferenceDto> OfferedThings { get; set; } = new();

    [DataMember(Name = "requestedThings")]
    public List<ModThingReferenceDto> RequestedThings { get; set; } = new();

    [DataMember(Name = "feeSilver")]
    public int FeeSilver { get; set; }

    [DataMember(Name = "allowSelfPickup")]
    public bool AllowSelfPickup { get; set; }

    [DataMember(Name = "allowServerDropPod")]
    public bool AllowServerDropPod { get; set; }
}

[DataContract]
public sealed class ModQuoteTradeOrderFeeRequestDto
{
    [DataMember(Name = "owner")]
    public ModProtocolIdentityDto? Owner { get; set; }

    [DataMember(Name = "offeredThings")]
    public List<ModThingReferenceDto> OfferedThings { get; set; } = new();

    [DataMember(Name = "requestedThings")]
    public List<ModThingReferenceDto> RequestedThings { get; set; } = new();
}

[DataContract]
public sealed class ModTradeOrderFeeQuoteResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "requiredFeeSilver")]
    public int RequiredFeeSilver { get; set; }

    [DataMember(Name = "missingMarketValueDefs")]
    public List<string> MissingMarketValueDefs { get; set; } = new();
}

[DataContract]
public sealed class ModCreateTradeOrderWithSnapshotRequestDto
{
    [DataMember(Name = "tradeOrder")]
    public ModCreateTradeOrderRequestDto? TradeOrder { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}

[DataContract]
public sealed class ModStorePawnPackageRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "owner")]
    public ModProtocolIdentityDto? Owner { get; set; }

    [DataMember(Name = "pawnPackage")]
    public ModPawnExchangePackageDto? PawnPackage { get; set; }
}

[DataContract]
public sealed class ModStorePawnPackageResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "pawnPackageId")]
    public string? PawnPackageId { get; set; }

    [DataMember(Name = "pawnGlobalId")]
    public string? PawnGlobalId { get; set; }
}

[DataContract]
public sealed class ModGetPawnPackageRequestDto
{
    [DataMember(Name = "requester")]
    public ModProtocolIdentityDto? Requester { get; set; }

    [DataMember(Name = "pawnPackageId")]
    public string PawnPackageId { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModGetPawnPackageResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "pawnPackageId")]
    public string? PawnPackageId { get; set; }

    [DataMember(Name = "pawnPackage")]
    public ModPawnExchangePackageDto? PawnPackage { get; set; }
}

[DataContract]
public sealed class ModStoreThingPackageRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "owner")]
    public ModProtocolIdentityDto? Owner { get; set; }

    [DataMember(Name = "thingPackage")]
    public ModThingStatePackageDto? ThingPackage { get; set; }
}

[DataContract]
public sealed class ModStoreThingPackageResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "thingPackageId")]
    public string? ThingPackageId { get; set; }

    [DataMember(Name = "fingerprint")]
    public string? Fingerprint { get; set; }
}

[DataContract]
public sealed class ModGetThingPackageRequestDto
{
    [DataMember(Name = "requester")]
    public ModProtocolIdentityDto? Requester { get; set; }

    [DataMember(Name = "thingPackageId")]
    public string ThingPackageId { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModGetThingPackageResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "thingPackageId")]
    public string? ThingPackageId { get; set; }

    [DataMember(Name = "thingPackage")]
    public ModThingStatePackageDto? ThingPackage { get; set; }
}

[DataContract]
public sealed class ModListTradeOrdersRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "scope")]
    public string Scope { get; set; } = "Open";

    [DataMember(Name = "offset")]
    public int Offset { get; set; }

    [DataMember(Name = "limit")]
    public int Limit { get; set; } = 10;
}

[DataContract]
public sealed class ModTradeOrderSummaryDto
{
    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "owner")]
    public ModProtocolIdentityDto? Owner { get; set; }

    [DataMember(Name = "counterparty")]
    public ModProtocolIdentityDto? Counterparty { get; set; }

    [DataMember(Name = "offeredThings")]
    public List<ModThingReferenceDto> OfferedThings { get; set; } = new();

    [DataMember(Name = "requestedThings")]
    public List<ModThingReferenceDto> RequestedThings { get; set; } = new();

    [DataMember(Name = "feeSilver")]
    public int FeeSilver { get; set; }

    [DataMember(Name = "allowSelfPickup")]
    public bool AllowSelfPickup { get; set; }

    [DataMember(Name = "allowServerDropPod")]
    public bool AllowServerDropPod { get; set; }

    [DataMember(Name = "acceptedMemoCount")]
    public int AcceptedMemoCount { get; set; }

    [DataMember(Name = "createdAtUtc")]
    public string? CreatedAtUtc { get; set; }

    [DataMember(Name = "expiresAtUtc")]
    public string? ExpiresAtUtc { get; set; }

    [DataMember(Name = "status")]
    public string Status { get; set; } = string.Empty;

    [DataMember(Name = "viewerHasAccepted")]
    public bool ViewerHasAccepted { get; set; }

    [DataMember(Name = "viewerAcceptedMemoEventId")]
    public string? ViewerAcceptedMemoEventId { get; set; }

    [DataMember(Name = "serverDropPodPostage")]
    public ModTradePostageQuoteDto? ServerDropPodPostage { get; set; }

    [DataMember(Name = "targetContext")]
    public ModEventTargetContextDto? TargetContext { get; set; }
}

[DataContract]
public sealed class ModTradePostageQuoteDto
{
    [DataMember(Name = "reachable")]
    public bool Reachable { get; set; }

    [DataMember(Name = "postageSilver")]
    public int? PostageSilver { get; set; }

    [DataMember(Name = "distanceTiles")]
    public int? DistanceTiles { get; set; }

    [DataMember(Name = "status")]
    public string Status { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModListTradeOrdersResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "orders")]
    public List<ModTradeOrderSummaryDto> Orders { get; set; } = new();

    [DataMember(Name = "tradeMarketplaceEnabled")]
    public bool TradeMarketplaceEnabled { get; set; } = true;

    [DataMember(Name = "totalCount")]
    public int TotalCount { get; set; }

    [DataMember(Name = "offset")]
    public int Offset { get; set; }

    [DataMember(Name = "limit")]
    public int Limit { get; set; }

    [DataMember(Name = "hasMore")]
    public bool HasMore { get; set; }
}

[DataContract]
public sealed class ModAcceptTradeOrderRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "tradeEventId")]
    public string TradeEventId { get; set; } = string.Empty;

    [DataMember(Name = "acceptor")]
    public ModProtocolIdentityDto? Acceptor { get; set; }

    [DataMember(Name = "postagePaidByAcceptor")]
    public bool PostagePaidByAcceptor { get; set; }
}

[DataContract]
public sealed class ModAcceptTradeOrderResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "tradeEventId")]
    public string? TradeEventId { get; set; }

    [DataMember(Name = "memoEventId")]
    public string? MemoEventId { get; set; }

    [DataMember(Name = "memoCreated")]
    public bool MemoCreated { get; set; }
}

[DataContract]
public sealed class ModFulfillTradeOrderRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "tradeEventId")]
    public string TradeEventId { get; set; } = string.Empty;

    [DataMember(Name = "acceptedMemoEventId")]
    public string AcceptedMemoEventId { get; set; } = string.Empty;

    [DataMember(Name = "acceptor")]
    public ModProtocolIdentityDto? Acceptor { get; set; }

    [DataMember(Name = "deliveredThings")]
    public List<ModThingReferenceDto> DeliveredThings { get; set; } = new();

    [DataMember(Name = "fulfillmentMode")]
    public string FulfillmentMode { get; set; } = "SelfDelivery";
}

[DataContract]
public sealed class ModFulfillTradeOrderWithSnapshotRequestDto
{
    [DataMember(Name = "fulfillment")]
    public ModFulfillTradeOrderRequestDto? Fulfillment { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}

[DataContract]
public sealed class ModFulfillTradeOrderResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "tradeEventId")]
    public string? TradeEventId { get; set; }

    [DataMember(Name = "acceptedMemoEventId")]
    public string? AcceptedMemoEventId { get; set; }

    [DataMember(Name = "exchangeEventId")]
    public string? ExchangeEventId { get; set; }

    [DataMember(Name = "exchangeCreated")]
    public bool ExchangeCreated { get; set; }

    [DataMember(Name = "receivedThings")]
    public List<ModThingReferenceDto> ReceivedThings { get; set; } = new();

    [DataMember(Name = "missingRequirements")]
    public List<string> MissingRequirements { get; set; } = new();

    [DataMember(Name = "tradeStatus")]
    public string TradeStatus { get; set; } = string.Empty;

    [DataMember(Name = "acceptorDeliveryEventId")]
    public string? AcceptorDeliveryEventId { get; set; }

    [DataMember(Name = "ownerDeliveryEventId")]
    public string? OwnerDeliveryEventId { get; set; }

    [DataMember(Name = "appliedSnapshotId")]
    public string? AppliedSnapshotId { get; set; }

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }
}

[DataContract]
public sealed class ModCloseTradeOrderRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "tradeEventId")]
    public string TradeEventId { get; set; } = string.Empty;

    [DataMember(Name = "owner")]
    public ModProtocolIdentityDto? Owner { get; set; }

    [DataMember(Name = "reason")]
    public string? Reason { get; set; }
}

[DataContract]
public sealed class ModCloseTradeOrderResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "tradeEventId")]
    public string? TradeEventId { get; set; }

    [DataMember(Name = "terminalStatus")]
    public string TerminalStatus { get; set; } = string.Empty;

    [DataMember(Name = "notifiedAcceptorCount")]
    public int NotifiedAcceptorCount { get; set; }
}
