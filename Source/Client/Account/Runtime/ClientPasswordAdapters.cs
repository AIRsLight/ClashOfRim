using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Accounts;
using AIRsLight.ClashOfRim.Modules.Transport;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Account;

internal sealed class ClientPasswordStore : IClientPasswordStore
{
    private readonly ClashOfRimSettings settings;
    internal ClientPasswordStore(ClashOfRimSettings settings) => this.settings = settings;
    public void Write(string password) => settings.OfflinePassword = password;
    public void Persist() => settings.Write();
}
internal sealed class ClientPasswordTransport : IClientPasswordTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientPasswordTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public async Task<ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>> ChangeAsync(
        ClashOfRimClientNetworkContext context, string? currentPassword, string? newPassword, CancellationToken token)
    {
        using var http = new HttpClient();
        var protocol = new ClientAccountProtocol(new ClientProtocolTransport(http, context, environment),
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await protocol.ChangeAsync(context, currentPassword, newPassword, token);
    }
}
internal sealed class ClientPasswordPresentation : IClientPasswordPresentation
{
    public string Format(ClientPasswordNotice notice) => notice.Stage == ClientPasswordStage.Changed && !string.IsNullOrWhiteSpace(notice.Message)
        ? notice.Message! : ClashOfRimText.Key("ClashOfRim.Account.Password" + notice.Stage,
            (notice.Code ?? "").Named("CODE"), (notice.Message ?? notice.Exception?.Message ?? "").Named("MESSAGE"),
            (notice.Exception?.GetType().Name ?? "").Named("TYPE"));
    public void Report(ClientPasswordNotice notice, string message)
    {
        if (notice.Stage == ClientPasswordStage.NoSession) Messages.Message(message, MessageTypeDefOf.RejectInput, historical: false);
        if (notice.Stage == ClientPasswordStage.Changed) Messages.Message(message, MessageTypeDefOf.PositiveEvent, historical: false);
        if (notice.Exception is not null) Log.Warning("[ClashOfRim] Offline password change failed: " + notice.Exception);
    }
}
