using System;
using System.Globalization;
using HarmonyLib;
using RimWorld.Planet;

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

    internal static int? ReadFirstInt(object? target, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ReadMember(target, name);
            if (value is int intValue)
            {
                return intValue;
            }

            if (value is PlanetTile planetTile)
            {
                return planetTile.Valid ? planetTile.tileId : null;
            }

            object? tileId = ReadMember(value, "tileId")
                ?? ReadMember(value, "TileId")
                ?? ReadMember(value, "id")
                ?? ReadMember(value, "Id");
            if (tileId is int reflectedTileId)
            {
                return reflectedTileId;
            }

            if (value is not null
                && int.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    internal static int ReadFirstTileLayerId(object? target, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ReadMember(target, name);
            if (value is PlanetTile planetTile)
            {
                return ReadPlanetTileLayerId(planetTile);
            }

            if (value is not null && PlanetTile.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out PlanetTile parsed))
            {
                return ReadPlanetTileLayerId(parsed);
            }
        }

        return 0;
    }

    internal static int ReadPlanetTileLayerId(PlanetTile tile)
    {
        object? layerId = ReadMember(tile, "layerId");
        if (layerId is int reflectedLayerId)
        {
            return Math.Max(0, reflectedLayerId);
        }

        try
        {
            return tile.Valid ? Math.Max(0, tile.Layer.LayerID) : 0;
        }
        catch
        {
            return 0;
        }
    }

    internal static bool? ReadFirstBool(object? target, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ReadMember(target, name);
            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is not null
                && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out bool parsed))
            {
                return parsed;
            }
        }

        return null;
    }

}
