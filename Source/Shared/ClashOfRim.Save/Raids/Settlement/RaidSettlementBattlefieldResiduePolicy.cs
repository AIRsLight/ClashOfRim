namespace AIRsLight.ClashOfRim.Save;

internal static class RaidSettlementBattlefieldResiduePolicy
{
    public static bool IsBattlefieldResidue(string? className, string? defName)
    {
        if (string.IsNullOrWhiteSpace(defName))
        {
            return false;
        }

        if (string.Equals(className, "Filth", StringComparison.Ordinal))
        {
            return true;
        }

        if (defName.StartsWith("Filth_", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(defName, "SandbagRubble", StringComparison.Ordinal);
    }

    public static string ResidueKey(string? defName, string? position)
    {
        if (string.IsNullOrWhiteSpace(defName) || string.IsNullOrWhiteSpace(position))
        {
            return string.Empty;
        }

        return defName.Trim() + "@" + NormalizeCell(position);
    }

    private static string NormalizeCell(string value)
    {
        return new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
    }
}
