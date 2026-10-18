using System;
using System.Globalization;
using HarmonyLib;

namespace AIRsLight.ClashOfRim;

// Shared reflection boundary for supported runtime member names.
internal static class ClientWorldRuntimeValues
{
    internal static string? ReadFirstString(object? target, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ReadMember(target, name);
            if (value is not null)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    internal static object? ReadMember(object? target, string name)
    {
        if (target is null)
        {
            return null;
        }

        Type type = target.GetType();
        try
        {
            return AccessTools.Property(type, name)?.GetValue(target, null)
                ?? AccessTools.Field(type, name)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    internal static string? ReadDefName(object? def)
    {
        return ReadFirstString(def, "defName", "DefName");
    }

}
