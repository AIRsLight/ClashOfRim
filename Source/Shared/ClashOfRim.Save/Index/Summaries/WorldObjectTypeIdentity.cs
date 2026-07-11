namespace AIRsLight.ClashOfRim.Save;

public static class WorldObjectTypeIdentity
{
    public const string SettlementType = "Settlement";
    public const string PlayerColonyDef = "PlayerColony";

    public static bool IsSettlement(WorldObjectSummary worldObject)
    {
        ArgumentNullException.ThrowIfNull(worldObject);
        return IsExact(worldObject.Def, SettlementType)
            || IsExact(worldObject.Class, SettlementType);
    }

    public static bool IsPlayerColonyMarker(WorldObjectSummary worldObject)
    {
        ArgumentNullException.ThrowIfNull(worldObject);
        return IsExact(worldObject.Def, PlayerColonyDef);
    }

    public static bool IsExact(string? value, string expected)
    {
        return string.Equals(value, expected, StringComparison.Ordinal);
    }
}
