using System.Runtime.Serialization;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[DataContract]
public sealed class ModWorldPollutedTileDto
{
    [DataMember(Name = "tile")]
    public int Tile { get; set; }

    [DataMember(Name = "pollution")]
    public float Pollution { get; set; }
}
