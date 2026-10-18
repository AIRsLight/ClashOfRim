using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

// A decoded accepted login has its own authenticated identity, including when
// cancellation prevents adoption. Cleanup must never borrow the next session.
internal sealed record ClientSessionCredential(ClashOfRimClientNetworkContext Context, ModLoginResponseDto Receipt)
{
    internal static ClientSessionCredential? Capture(ClashOfRimClientNetworkContext expected, ModLoginResponseDto receipt)
    {
        if (receipt.Result?.Accepted != true || string.IsNullOrWhiteSpace(receipt.AuthToken) || string.IsNullOrWhiteSpace(receipt.SessionId)) return null;
        return new(new ClashOfRimClientNetworkContext(expected.ServerBaseUrl,
            string.IsNullOrWhiteSpace(receipt.AuthenticatedUserId) ? expected.UserId : receipt.AuthenticatedUserId!.Trim(),
            expected.ColonyId, expected.CurrentSnapshotId, expected.SteamAuthTicket, expected.OfflinePassword, receipt.AuthToken), receipt);
    }
}
