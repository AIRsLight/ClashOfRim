using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionExitCallbacks
{
    internal static ClientSessionExitCallbacks? Current { get; private set; }
    internal static void Attach(ClientSessionExitCallbacks? value) => Current = value;
    private readonly ClientSessionLifecycle session;
    internal ClientSessionExitCallbacks(ClientSessionLifecycle session) => this.session = session;
    internal Task EndAsync() => ReferenceEquals(Current, this) ? session.End(sendLogout: true) : Task.CompletedTask;
}
