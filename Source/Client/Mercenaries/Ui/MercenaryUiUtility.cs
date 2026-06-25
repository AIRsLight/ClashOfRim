using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

internal static class MercenaryUiUtility
{
    public static string BuildQuoteRequestKey(string skillDefName, int skillLevel, int durationDays, string? snapshotId)
    {
        return $"{skillDefName}|{skillLevel}|{durationDays}|{snapshotId}";
    }

    public static string FormatDurationLine(int days)
    {
        return ClashOfRimText.Key("ClashOfRim.Mercenary.DurationLine", days.Named("DAYS"));
    }

    public static string FormatPriceLine(int priceSilver)
    {
        return ClashOfRimText.Key("ClashOfRim.Mercenary.PriceLine", priceSilver.Named("PRICE"));
    }

    public static string FormatQuoteStatus(string? status)
    {
        return string.IsNullOrWhiteSpace(status)
            ? ClashOfRimText.Key("ClashOfRim.Mercenary.PriceUnavailable")
            : status!;
    }

    public static string FormatConfirmHire(
        MercenaryProfession? profession,
        string fallbackSkillDefName,
        int skillLevel,
        int durationDays,
        int priceSilver)
    {
        string label = profession?.Label ?? MercenarySkillUtility.ProfessionLabel(fallbackSkillDefName);
        return ClashOfRimText.Key(
            "ClashOfRim.Mercenary.ConfirmHireWithPrice",
            label.Named("SKILL"),
            MercenarySkillUtility.TierLabel(skillLevel).Named("TIER"),
            durationDays.Named("DAYS"),
            priceSilver.Named("PRICE"));
    }
}
