using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;

internal static class GiftRejectionTestClient
{
    public static RejectGiftRequest Request(AuthoritativeEvent gift, string? token, string reason = "first") =>
        new(gift.EventId, "receiver", "away", "base", reason, token);
    public static RejectGiftResponse Reject(ClashOfRimNetworkState state, RejectGiftRequest request)
        => state.Gifts.Reject(request);

}
