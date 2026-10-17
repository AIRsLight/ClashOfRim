using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

internal interface IClientSessionAuthentication
{
    // Receipt capture runs before cancellation can discard an already decoded credential.
    Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context,
        string purpose, string? manifest, CancellationToken token, Action<ModLoginResponseDto>? captureReceipt = null);
    Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context,
        string? sessionId, CancellationToken token, bool bindToSession = true);
}
