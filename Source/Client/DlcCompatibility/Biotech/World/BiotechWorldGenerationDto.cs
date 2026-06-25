using System.Runtime.Serialization;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[DataContract]
public sealed class ModBiotechWorldGenerationDto
{
    [DataMember(Name = "pollution")]
    public string? Pollution { get; set; }
}
