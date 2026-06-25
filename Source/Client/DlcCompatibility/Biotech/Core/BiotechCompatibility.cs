using AIRsLight.ClashOfRim.ThirdPartyCompatibility;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    public static bool HasBiotechPawnExchange => ClashOfRimCompatibilityApi.HasCompatibilityCapability(BiotechCompatibilityKeys.PawnExchange);

    public static bool HasBiotechTradeMetadata => ClashOfRimCompatibilityApi.HasCompatibilityCapability(BiotechCompatibilityKeys.TradeMetadata);

    public static bool HasBiotechWorldPollution => ClashOfRimCompatibilityApi.HasCompatibilityCapability(BiotechCompatibilityKeys.WorldPollution);
}
