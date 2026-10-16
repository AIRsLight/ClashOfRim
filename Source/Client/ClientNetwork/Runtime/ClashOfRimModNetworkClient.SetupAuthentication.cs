using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIRsLight.ClashOfRim.ClientNetwork;

public sealed partial class ClashOfRimModNetworkClient
{
    private async Task<ClashOfRimClientNetworkResult<TResponse>> WithSetupAuthenticationAsync<TResponse>(
        string purpose,
        Func<ClashOfRimModNetworkClient, CancellationToken, Task<ClashOfRimClientNetworkResult<TResponse>>> operation,
        CancellationToken cancellationToken)
    {
        if (!context.IsConfigured) return await NotConfigured<TResponse>();
        if (!string.IsNullOrWhiteSpace(context.AuthToken))
            return await operation(this, cancellationToken).ConfigureAwait(false);

        // Initial setup and compatibility updates can precede a normal map session.
        // Keep this session private; shared settings and an existing session are never replaced.
        var login = await LoginAsync(purpose, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!login.Success || login.Response?.Result?.Accepted != true
            || string.IsNullOrWhiteSpace(login.Response.AuthToken) || string.IsNullOrWhiteSpace(login.Response.SessionId))
            return ClashOfRimClientNetworkResult<TResponse>.Failed(login.ErrorCode ?? "SetupLoginRejected",
                login.Message ?? login.Response?.Result?.Message ?? "World setup login failed.");

        var authenticated = new ClashOfRimModNetworkClient(httpClient, new ClashOfRimClientNetworkContext(
            context.ServerBaseUrl, context.UserId, context.ColonyId, context.CurrentSnapshotId,
            context.SteamAuthTicket, context.OfflinePassword, login.Response.AuthToken));
        try { return await operation(authenticated, cancellationToken).ConfigureAwait(false); }
        finally
        {
            // Release only the session created here, even if the owning client work was cancelled.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await authenticated.PostAsync<ModLogoutRequestDto, ModLogoutResponseDto>(LogoutRoute,
                new ModLogoutRequestDto { UserId = context.UserId, ColonyId = context.ColonyId, SessionId = login.Response.SessionId },
                cleanup.Token, bindToSession: false).ConfigureAwait(false);
        }
    }
}
