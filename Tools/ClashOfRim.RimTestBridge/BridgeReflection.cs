using HarmonyLib;

namespace ClashOfRim.RimTestBridge;

// Private engine/editor access stays in the test adapter, never in the product
// modules. Domain observations should prefer their typed read interfaces.
internal static class BridgeReflection
{
    internal static T Field<T>(object target, string name) => (T)AccessTools.Field(target.GetType(), name).GetValue(target)!;
    internal static void Set(object target, string name, object? value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
    internal static object? Invoke(object target, string name, params object[] args) => AccessTools.Method(target.GetType(), name).Invoke(target, args);
    internal static object? InvokeStatic(Type type, string name, params object[] args) => AccessTools.Method(type, name).Invoke(null, args);
}
