using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

// Each invocation owns its temporary credential until the operation and bounded cleanup finish.
// Existing credentials are borrowed; caller settings and the normal session are never replaced.
internal sealed class ClientSetupAuthenticationLease
{
    private readonly IClientSessionAuthentication authentication;
    private readonly ClientProtocolTransport transport;
    private readonly IClientAuthenticationDiagnostics diagnostics;
    private readonly TimeSpan cleanupTimeout;

    internal ClientSetupAuthenticationLease(IClientSessionAuthentication authentication, ClientProtocolTransport transport,
        IClientAuthenticationDiagnostics diagnostics, TimeSpan? cleanupTimeout = null)
    {
        this.authentication = authentication; this.transport = transport; this.diagnostics = diagnostics;
        this.cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(15);
    }

    internal async Task<ClashOfRimClientNetworkResult<T>> RunAsync<T>(ClashOfRimClientNetworkContext context, string purpose,
        Func<ClashOfRimClientNetworkContext, ClientProtocolTransport, CancellationToken, Task<ClashOfRimClientNetworkResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        if (!context.IsConfigured) return ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", diagnostics.NotConfiguredMessage);
        CancellationToken owner = ClientSessionWork.Token;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner);
        CancellationToken token = cancellation.Token;
        ClashOfRimClientNetworkContext? owned = null;
        string? sessionId = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(context.AuthToken))
            {
                var login = await authentication.LoginAsync(context, purpose, null, token, receipt =>
                {
                    if (receipt.Result?.Accepted != true || string.IsNullOrWhiteSpace(receipt.AuthToken) || string.IsNullOrWhiteSpace(receipt.SessionId)) return;
                    owned = new ClashOfRimClientNetworkContext(context.ServerBaseUrl,
                        string.IsNullOrWhiteSpace(receipt.AuthenticatedUserId) ? context.UserId : receipt.AuthenticatedUserId!.Trim(),
                        context.ColonyId, context.CurrentSnapshotId, context.SteamAuthTicket, context.OfflinePassword, receipt.AuthToken);
                    sessionId = receipt.SessionId;
                }).ConfigureAwait(false);
                if (!login.Success || owned is null)
                    return ClashOfRimClientNetworkResult<T>.Failed(login.ErrorCode ?? "SetupLoginRejected",
                        login.Message ?? login.Response?.Result?.Message ?? "World setup login failed.");
            }
            token.ThrowIfCancellationRequested();
            if (owner != ClientSessionWork.Token || !ClientSessionWork.IsCurrent) throw new OperationCanceledException("The multiplayer session ended.", owner);
            var result = await operation(owned ?? context, transport.ForContext(owned ?? context), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException ex) { return ClashOfRimClientNetworkResult<T>.Failed(ex.GetType().Name, ex.Message); }
        finally
        {
            if (owned is not null)
            {
                using var cleanup = new CancellationTokenSource(cleanupTimeout);
                var result = await authentication.LogoutAsync(owned, sessionId, cleanup.Token, bindToSession: false).ConfigureAwait(false);
                if (!result.Success || result.Response?.Result?.Accepted != true)
                    diagnostics.CleanupFailed(result.ErrorCode, result.Message ?? result.Response?.Result?.Message);
            }
        }
    }
}
