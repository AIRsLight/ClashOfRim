using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Save;

public interface ISaveIndexDataExtension
{
    IEnumerable<SaveIndexExtensionData> ReadIndexExtensions(
        XElement? world,
        SaveIndexReadContext context);
}
