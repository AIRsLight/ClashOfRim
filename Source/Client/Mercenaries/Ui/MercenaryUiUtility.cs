using AIRsLight.ClashOfRim.Modules.Mercenaries;
using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

internal static class MercenaryUiUtility
{
    internal static string GuardTierLabel(string tier) => ClientMercenarySelection.NormalizeGuardTier(tier) switch
    {
        "Skilled" => ClashOfRimText.Key("ClashOfRim.Mercenary.GuardScaleStandard"),
        "Master" => ClashOfRimText.Key("ClashOfRim.Mercenary.GuardScaleLarge"),
        _ => ClashOfRimText.Key("ClashOfRim.Mercenary.GuardScaleSmall")
    };

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
