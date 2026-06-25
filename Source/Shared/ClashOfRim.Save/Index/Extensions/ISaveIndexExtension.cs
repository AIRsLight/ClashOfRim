using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Save;

public interface ISaveIndexExtension
{
    IEnumerable<ThingSummary> ReadContainedThings(
        XElement containerThing,
        ThingSummary container,
        SaveIndexReadContext context);
}
