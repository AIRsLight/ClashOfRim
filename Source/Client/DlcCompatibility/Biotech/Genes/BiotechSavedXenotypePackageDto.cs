using System.Runtime.Serialization;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[DataContract]
internal sealed class BiotechSavedXenotypePackageDto
{
    [DataMember(Name = "name")]
    public string? Name { get; set; }

    [DataMember(Name = "xml")]
    public string Xml { get; set; } = string.Empty;

    [DataMember(Name = "xmlSha256")]
    public string? XmlSha256 { get; set; }
}
