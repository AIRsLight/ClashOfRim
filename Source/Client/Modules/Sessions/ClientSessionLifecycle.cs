using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;

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
    private InvalidationHold? invalidationHold;
    private Exit? continuingExit;
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
        return SameIdentity(current, expected)
            && (!requireSnapshot || current.CurrentSnapshotId == expected.CurrentSnapshotId);
    }
    private static bool SameIdentity(ClashOfRimClientNetworkContext current, ClashOfRimClientNetworkContext expected)
        => current.ServerBaseUrl == expected.ServerBaseUrl && current.UserId == expected.UserId
            && current.ColonyId == expected.ColonyId && current.AuthToken == expected.AuthToken;
    internal bool Owns(Login login) => Owns(login.Owner, login.Context, false);
    internal bool Holds(ClientSessionCredential credential)
        => sessionId == credential.Receipt.SessionId && SameIdentity(ReadContext(), credential.Context);
    internal bool Continues(Login login, Ownership previousOwner, ClashOfRimClientNetworkContext previousContext)
        => previousOwner.Token == login.PreviousOwner.Token && ReferenceEquals(previousOwner.Identity, login.PreviousOwner.Identity)
            && SameIdentity(previousContext, login.PreviousContext) && Owns(login);
    internal event Action<ModLoginResponseDto?>? IdentityChanged;
    // Domain state must be cleared before the credential write persists the exit.
    internal event Action? Ended;
    internal event Action<string>? LogoutFailed;

    internal Login? PublishLogin(ModLoginResponseDto response)
    {
        using var p = ClientSessionWork.EnterPublication();
        return PublishLogin(CaptureOwnership(), ReadContext(), response);
    }

    internal Login? PublishLogin(Ownership previousOwner, ClashOfRimClientNetworkContext previousContext, ModLoginResponseDto source, Action<Login>? capturePublication = null)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (!Owns(previousOwner, previousContext, false)) return null;
        var response = ClientProtocolJson.Copy(source);
        connection.Stop(reportStatus: false);
        if (!Owns(previousOwner, previousContext, false)) return null;
        identityOwner = new object();
        pendingInvalidation = null;
        invalidationHold = null;
        unavailable = false;
        sessionId = response.SessionId;
        var context = new ClashOfRimClientNetworkContext(previousContext.ServerBaseUrl, NormalizeName(response.AuthenticatedUserId) ?? previousContext.UserId,
            previousContext.ColonyId, previousContext.CurrentSnapshotId, previousContext.SteamAuthTicket, previousContext.OfflinePassword, response.AuthToken ?? string.Empty);
        var accepted = new Login(previousOwner, previousContext, CaptureOwnership(), context);
        capturePublication?.Invoke(accepted);
        identities.Write(NormalizeName(response.AuthenticatedUserId), NormalizeName(response.DisplayName), response.AuthToken ?? string.Empty);
        foreach (Action<ModLoginResponseDto?> changed in IdentityChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            if (!Owns(accepted)) break;
            changed(ClientProtocolJson.Copy(response));
        }
        // A reentrant callback can already supersede this login. Return its exact
        // receipt, never adopt whatever identity happens to be current afterwards.
        return accepted;
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
        => CompleteUnavailableOwned(invalidation) is not null;

    internal Exit? CompleteUnavailableOwned(Invalidation invalidation)
    {
        Exit? completed = null;
        ClientSessionWork.TryApply(() =>
        {
            if (!ReferenceEquals(pendingInvalidation, invalidation) || invalidation.Owner != ClientSessionWork.Token || HoldingInvalidation) return;
            completed = ClearIdentityCore();
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
        Ownership owner; ClashOfRimClientNetworkContext context;
        using (ClientSessionWork.EnterPublication())
        {
            // GenScene's disconnect hook can run inside an already completed,
            // owned menu transition. Do not replay its cleanup in that call.
            if (continuingExit is { } exit && Owns(exit.Owner, exit.Context, true)) return Task.CompletedTask;
            owner = CaptureOwnership(); context = ReadContext();
        }
        return EndCore(owner, context, false, sendLogout).Logout;
    }

    internal Exit? EndOwned(Ownership owner, ClashOfRimClientNetworkContext context, bool requireSnapshot = false)
        => EndCore(owner, context, requireSnapshot, sendLogout: false).Receipt;

    private (Exit? Receipt, Task Logout) EndCore(Ownership owner, ClashOfRimClientNetworkContext context, bool requireSnapshot, bool sendLogout)
    {
        string? previousSessionId = null;
        if (!ClientSessionWork.TryReset(() =>
        {
            if (!Owns(owner, context, requireSnapshot)) return false;
            previousSessionId = sessionId; return true;
        }, out var generation)) return (null, Task.CompletedTask);
        var ending = new Ownership(owner.Identity, generation);
        Exit? receipt = null;
        using (ClientSessionWork.EnterPublication())
        {
            // Cancellation and connection callbacks can synchronously log in again.
            // Only the identity that won the generation transition may be cleared.
            if (Owns(ending, context, requireSnapshot))
            {
                ending = DetachIdentity();
                bool Current() => Owns(ending, context, false);
                if (Current())
                {
                    unavailable = false;
                    foreach (Action ended in Ended?.GetInvocationList() ?? Array.Empty<Delegate>())
                    { if (!Current()) break; ended(); }
                    if (Current())
                        receipt = ClearAuthentication(ending, context);
                }
            }
        }

        // Logout deliberately outlives the exited generation, using its captured credentials.
        return (receipt, sendLogout && context.IsConfigured && !string.IsNullOrWhiteSpace(previousSessionId)
            ? Task.Run(() => ReleaseSessionAsync(context, previousSessionId!)) : Task.CompletedTask);
    }

    internal bool ContinueExit(Exit exit, Action action)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (!Owns(exit.Owner, exit.Context, true)) return false;
        var previous = continuingExit; continuingExit = exit;
        try { action(); return Owns(exit.Owner, exit.Context, true); }
        finally { continuingExit = previous; }
    }

    // A server-side terminal operation may close its stream before its HTTP receipt.
    // Keep that exact identity until the receipt is handled, then resume any expiry.
    internal InvalidationHold? HoldInvalidation(Ownership owner, ClashOfRimClientNetworkContext context, Func<bool>? current = null)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (unavailable || !Owns(owner, context, false) || HoldingInvalidation) return null;
        if (current?.Invoke() == false) return null;
        return invalidationHold = new(this, owner, context, current);
    }
    private bool HoldingInvalidation => invalidationHold is { } hold && Owns(hold.Owner, hold.Context, false) && hold.Current?.Invoke() != false;
    internal void WhenUnavailableReady(Invalidation invalidation, Action dispatch)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (!ReferenceEquals(pendingInvalidation, invalidation) || invalidation.Owner != ClientSessionWork.Token) return;
        if (HoldingInvalidation) invalidation.Dispatch = dispatch;
        else dispatch();
    }
    private void ReleaseInvalidation(InvalidationHold hold)
    {
        ClientSessionWork.TryApply(() =>
        {
            if (!ReferenceEquals(invalidationHold, hold)) return;
            invalidationHold = null;
            if (!Owns(hold.Owner, hold.Context, false) || pendingInvalidation is not { } invalidation) return;
            var dispatch = invalidation.Dispatch; invalidation.Dispatch = null; dispatch?.Invoke();
        });
    }

    private Exit? ClearIdentityCore()
    {
        var context = ReadContext();
        return ClearAuthentication(DetachIdentity(), context);
    }
    private Exit? ClearAuthentication(Ownership owner, ClashOfRimClientNetworkContext context)
    {
        if (!Owns(owner, context, false)) return null;
        identities.Write(null, null, string.Empty);
        var cleared = new ClashOfRimClientNetworkContext(context.ServerBaseUrl, context.UserId, context.ColonyId,
            context.CurrentSnapshotId, context.SteamAuthTicket, context.OfflinePassword, string.Empty);
        if (!Owns(owner, cleared, false)) return null;
        var exitContext = ReadContext();
        foreach (Action<ModLoginResponseDto?> changed in IdentityChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        { if (!Owns(owner, exitContext, true)) return null; changed(null); }
        return Owns(owner, exitContext, true) ? new(owner, exitContext) : null;
    }

    private Ownership DetachIdentity()
    {
        sessionId = null;
        identityOwner = new object();
        pendingInvalidation = null;
        invalidationHold = null;
        var detached = CaptureOwnership();
        connection.Stop(reportStatus: false);
        return detached;
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
        internal Action? Dispatch { get; set; }
    }

    internal sealed record Exit(Ownership Owner, ClashOfRimClientNetworkContext Context);
    internal sealed record Login(Ownership PreviousOwner, ClashOfRimClientNetworkContext PreviousContext, Ownership Owner, ClashOfRimClientNetworkContext Context);
    internal sealed class InvalidationHold : IDisposable
    {
        private readonly ClientSessionLifecycle session;
        internal readonly Ownership Owner;
        internal readonly ClashOfRimClientNetworkContext Context;
        internal readonly Func<bool>? Current;
        internal InvalidationHold(ClientSessionLifecycle session, Ownership owner, ClashOfRimClientNetworkContext context, Func<bool>? current)
        { this.session = session; Owner = owner; Context = context; Current = current; }
        public void Dispose() => session.ReleaseInvalidation(this);
    }

    internal sealed class Ownership
    {
        internal Ownership(object identity, CancellationToken token) { Identity = identity; Token = token; }
        internal object Identity { get; }
        internal CancellationToken Token { get; }
    }
}
