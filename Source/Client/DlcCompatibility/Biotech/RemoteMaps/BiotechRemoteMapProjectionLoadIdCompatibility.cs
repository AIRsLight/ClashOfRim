using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    internal static string? BuildBiotechAreaLoadId(XElement area, int id)
    {
        if (!HasBiotechWorldPollution)
        {
            return null;
        }

        string className = area.Attribute("Class")?.Value ?? string.Empty;
        return className.Contains("Area_PollutionClear")
            ? "Area_" + id + "_PollutionClear"
            : null;
    }

}
