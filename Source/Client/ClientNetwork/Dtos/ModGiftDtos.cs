using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModCreateGiftRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "actor")]
    public ModProtocolIdentityDto? Actor { get; set; }

    [DataMember(Name = "target")]
    public ModProtocolIdentityDto? Target { get; set; }

    [DataMember(Name = "things")]
    public List<ModThingReferenceDto> Things { get; set; } = new();

    [DataMember(Name = "message")]
    public string? Message { get; set; }

    [DataMember(Name = "targetContext")]
    public ModEventTargetContextDto? TargetContext { get; set; }

    [DataMember(Name = "deliveryKind")]
    public string? DeliveryKind { get; set; }
}

[DataContract]
public sealed class ModCreateGiftWithSnapshotRequestDto
{
    [DataMember(Name = "gift")]
    public ModCreateGiftRequestDto? Gift { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}
