using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

// Owns authenticated identity publication and invalidation. ClientSessionWork remains the
// only generation boundary; a pending invalidation also belongs to one accepted identity.
internal sealed class ClientSessionLifecycle
{
    private readonly IClientSessionIdentityStore identities;
    private readonly IClientSessionLogoutTransport logout;
    private readonly ClientSessionConnection connection;
    private volatile string? sessionId;
    private volatile bool unavailable;
    private Invalidation? pendingInvalidation;
    private volatile object identityOwner = new();

    internal ClientSessionLifecycle(IClientSessionIdentityStore identities, IClientSessionLogoutTransport logout,
        ClientSessionConnection connection)
    {
        this.identities = identities;
        this.logout = logout;
        this.connection = connection;
    }

    internal string? SessionId => sessionId;
    internal bool IsUnavailable => unavailable;
    internal ClashOfRimClientNetworkContext ReadContext() => identities.ReadContext();
    internal Ownership CaptureOwnership() => new(identityOwner, ClientSessionWork.Token);
    internal bool Owns(Ownership owner) => ClientSessionWork.IsCurrent && owner.Token == ClientSessionWork.Token
        && ReferenceEquals(owner.Identity, identityOwner);
    internal bool Owns(Ownership owner, ClashOfRimClientNetworkContext expected, bool requireSnapshot)
    {
        if (!Owns(owner)) return false;
        var current = ReadContext();
        return current.ServerBaseUrl == expected.ServerBaseUrl && current.UserId == expected.UserId
            && current.ColonyId == expected.ColonyId && current.AuthToken == expected.AuthToken
            && (!requireSnapshot || current.CurrentSnapshotId == expected.CurrentSnapshotId);
    }
    internal event Action<ModLoginResponseDto?>? IdentityChanged;
    // Domain state must be cleared before the credential write persists the exit.
    internal event Action? Ended;
    internal event Action<string>? LogoutFailed;

    internal void PublishLogin(ModLoginResponseDto response)
    {
        using var publication = ClientSessionWork.EnterPublication();
        connection.Stop(reportStatus: false);
        identityOwner = new object();
        pendingInvalidation = null;
        unavailable = false;
        sessionId = response.SessionId;
        identities.Write(NormalizeName(response.AuthenticatedUserId), NormalizeName(response.DisplayName), response.AuthToken ?? string.Empty);
        IdentityChanged?.Invoke(response);
    }

    internal Invalidation? BeginUnavailable(string? observedAuthToken, string? observedSessionId)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (unavailable
            || (!string.IsNullOrWhiteSpace(observedAuthToken) && !string.Equals(observedAuthToken, identities.ReadContext().AuthToken, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(observedSessionId) && !string.Equals(observedSessionId, sessionId, StringComparison.Ordinal)))
            return null;

        unavailable = true;
        pendingInvalidation = new Invalidation(ClientSessionWork.Token);
        connection.Stop(reportStatus: false);
        return pendingInvalidation;
    }

    // Called by the main-thread continuation. A new login in the SAME generation cancels
    // this transition too, so an old queued expiry cannot clear the new credentials or UI.
    internal bool CompleteUnavailable(Invalidation invalidation)
    {
        bool completed = false;
        ClientSessionWork.TryApply(() =>
        {
            if (!ReferenceEquals(pendingInvalidation, invalidation) || invalidation.Owner != ClientSessionWork.Token) return;
            ClearIdentityCore();
            completed = true;
        });
        return completed;
    }

    internal void ClearIdentity()
    {
        using var publication = ClientSessionWork.EnterPublication();
        unavailable = false;
        ClearIdentityCore();
    }

    internal Task End(bool sendLogout)
    {
        ClientSessionWork.Reset();
        ClashOfRimClientNetworkContext context;
        string? previousSessionId;
        using (ClientSessionWork.EnterPublication())
        {
            context = identities.ReadContext();
            previousSessionId = sessionId;
            DetachIdentity();
            unavailable = false;
            Ended?.Invoke();
            identities.Write(null, null, string.Empty);
            IdentityChanged?.Invoke(null);
        }

        // Logout deliberately outlives the exited generation, using its captured credentials.
        return sendLogout && context.IsConfigured && !string.IsNullOrWhiteSpace(previousSessionId)
            ? Task.Run(() => ReleaseSessionAsync(context, previousSessionId!)) : Task.CompletedTask;
    }

    private void ClearIdentityCore()
    {
        DetachIdentity();
        identities.Write(null, null, string.Empty);
        IdentityChanged?.Invoke(null);
    }

    private void DetachIdentity()
    {
        connection.Stop(reportStatus: false);
        sessionId = null;
        identityOwner = new object();
        pendingInvalidation = null;
    }

    internal async Task ReleaseSessionAsync(ClashOfRimClientNetworkContext context, string previousSessionId)
    {
        try
        {
            var result = await logout.LogoutAsync(context, previousSessionId);
            if (!result.Success || result.Response?.Result?.Accepted != true)
                LogoutFailed?.Invoke("Explicit logout failed: "
                    + (result.Message ?? result.Response?.Result?.Message ?? result.ErrorCode ?? "unknown"));
        }
        catch (Exception ex) { LogoutFailed?.Invoke("Explicit logout exception: " + ex); }
    }

    private static string? NormalizeName(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    internal sealed class Invalidation
    {
        internal Invalidation(CancellationToken owner) => Owner = owner;
        internal CancellationToken Owner { get; }
    }

    internal sealed class Ownership
    {
        internal Ownership(object identity, CancellationToken token) { Identity = identity; Token = token; }
        internal object Identity { get; }
        internal CancellationToken Token { get; }
    }
}
