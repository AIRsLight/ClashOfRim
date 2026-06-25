using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModCreateSupportPawnRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "actor")]
    public ModProtocolIdentityDto? Actor { get; set; }

    [DataMember(Name = "target")]
    public ModProtocolIdentityDto? Target { get; set; }

    [DataMember(Name = "pawnGlobalKey")]
    public string PawnGlobalKey { get; set; } = string.Empty;

    [DataMember(Name = "sourceSnapshotId")]
    public string SourceSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "pawnName")]
    public string? PawnName { get; set; }

    [DataMember(Name = "temporaryControl")]
    public bool TemporaryControl { get; set; }

    [DataMember(Name = "expectedReturnAtUtc")]
    public string? ExpectedReturnAtUtc { get; set; }

    [DataMember(Name = "pawnReference")]
    public ModCrossMapPawnReferenceDto? PawnReference { get; set; }

    [DataMember(Name = "pawnPackage")]
    public ModPawnExchangePackageDto? PawnPackage { get; set; }

    [DataMember(Name = "targetContext")]
    public ModEventTargetContextDto? TargetContext { get; set; }

    [DataMember(Name = "sourceTile")]
    public int? SourceTile { get; set; }

    [DataMember(Name = "sourceCaravanLoadId")]
    public string? SourceCaravanLoadId { get; set; }

    [DataMember(Name = "permanentSupport")]
    public bool PermanentSupport { get; set; }

    [DataMember(Name = "supportDurationDays")]
    public int? SupportDurationDays { get; set; }

    [DataMember(Name = "expiresAtGameTicks")]
    public long? ExpiresAtGameTicks { get; set; }

    [DataMember(Name = "autoReturnOnSettlement")]
    public bool AutoReturnOnSettlement { get; set; }
}

[DataContract]
public sealed class ModCreateSupportPawnWithSnapshotRequestDto
{
    [DataMember(Name = "supportPawn")]
    public ModCreateSupportPawnRequestDto? SupportPawn { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}
