using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientServerMetadata
{
    Task Apply(string? manifest, bool administrator, bool refreshAdministrator, Func<bool> current);
}

// The server observations share publication order; each source keeps its own
// boundary (account identity for login, request/world for preparation/catalog).
internal sealed class ClientServerMetadata : IClientServerMetadata
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientCompatibilityBaseline compatibility;
    private readonly ClientAdministration administration;
    internal ClientServerMetadata(ClientSessionLifecycle session, ClientCompatibilityBaseline compatibility, ClientAdministration administration)
    { this.session = session; this.compatibility = compatibility; this.administration = administration; }
    internal void ObserveIdentity(ModLoginResponseDto? response)
    {
        using var p = ClientSessionWork.EnterPublication();
        var owner = session.CaptureOwnership(); var context = session.ReadContext();
        _ = Apply(response?.ServerCompatibilityManifestJson, response?.IsAdministrator == true, false,
            () => session.Owns(owner, context, false));
    }
    public Task Apply(string? manifest, bool administrator, bool refreshAdministrator, Func<bool> current)
    {
        using var p = ClientSessionWork.EnterPublication();
        compatibility.Apply(manifest, current);
        return current() ? administration.ObservePermission(administrator, refreshAdministrator, current) : Task.CompletedTask;
    }
}
