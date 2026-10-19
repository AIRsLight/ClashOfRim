using RimTest.Runtime;

namespace ClashOfRim.RimTestBridge;

internal static class BridgeRecords
{
    internal static object Record(this ObjectRegistry objects, object value) => new { reference = objects.Add(value), data = value };
}
