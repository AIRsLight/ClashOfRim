using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

internal interface IClientSessionLoginTransport
{
    Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context,
        string purpose, CancellationToken token, Action<ModLoginResponseDto> captureReceipt);
}

// An acquired credential belongs to the attempt until identity publication adopts
// it. Receipt capture precedes transport cancellation; cleanup uses that identity.
internal sealed class ClientSessionLogin
{
    private readonly ClientSessionLifecycle session;
    private readonly IClientSessionLoginTransport transport;
    internal ClientSessionLogin(ClientSessionLifecycle session, IClientSessionLoginTransport transport)
    { this.session = session; this.transport = transport; }
    internal Attempt Create(ClashOfRimClientNetworkContext context) => new(this, context);
    internal sealed class Attempt : IAsyncDisposable
    {
        private readonly ClientSessionLogin service;
        private readonly ClashOfRimClientNetworkContext expected;
        private bool adopted, disposed;
        internal ClientSessionCredential? Credential { get; private set; }
        internal ClientSessionLifecycle.Login? Publication { get; private set; }
        internal Attempt(ClientSessionLogin service, ClashOfRimClientNetworkContext expected) { this.service = service; this.expected = expected; }
        internal async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> RequestAsync(string purpose, CancellationToken token)
        {
            var result = await service.transport.LoginAsync(expected, purpose, token, Capture).ConfigureAwait(false);
            if (result.Success && result.Response is { } response) Capture(response);
            return result;
        }
        private void Capture(ModLoginResponseDto response) => Credential ??= ClientSessionCredential.Capture(expected, response);
        internal ClientSessionLifecycle.Login? Publish(ClientSessionLifecycle.Ownership owner)
        {
            using var publication = ClientSessionWork.EnterPublication();
            if (disposed || adopted || Credential is null) return null;
            try { return service.session.PublishLogin(owner, expected, Credential.Receipt, proof => Publication = proof); }
            finally { adopted = Publication is not null && service.session.Owns(Publication); }
        }
        public async ValueTask DisposeAsync()
        {
            ClientSessionCredential? release;
            using (ClientSessionWork.EnterPublication())
            {
                if (disposed) return;
                disposed = true;
                release = adopted ? null : Credential;
                // A store/identity observer can throw after adopting this exact
                // credential. Do not log out the live session in that case.
                if (release is not null && service.session.Holds(release)) release = null;
            }
            if (release is not null) await service.session.ReleaseSessionAsync(release.Context, release.Receipt.SessionId!).ConfigureAwait(false);
        }
    }
}
