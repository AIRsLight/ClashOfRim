using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientMercenaryTransport : IClientMercenaryQuoteTransport, IClientMercenaryHiringTransport, IClientMercenaryIncidentTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>> ReportAsync(ClashOfRimClientNetworkContext context,
        ClientMercenaryIncidentCommand command, CancellationToken token)
    {
        using var http = new HttpClient();
        return await Create(http, context).ReportMercenaryIncidentAsync(command.Key, command.GameTicks, command.ContractId, command.Kind, token);
    }
    public async Task<ClashOfRimClientNetworkResult<ModMercenaryQuoteResponseDto>> QuoteAsync(ClashOfRimClientNetworkContext context,
        long ticks, string skill, int level, int days, CancellationToken token)
    { using var http = new HttpClient(); return await Create(http, context).QuoteMercenaryAsync(ticks, skill, level, days, token); }

    public async Task<ClashOfRimClientNetworkResult<ModMercenaryGuardQuoteResponseDto>> QuoteGuardAsync(ClashOfRimClientNetworkContext context,
        long ticks, string tier, CancellationToken token)
    { using var http = new HttpClient(); return await Create(http, context).QuoteMercenaryGuardAsync(ticks, tier, token); }

    public async Task<ClashOfRimClientNetworkResult<ClientMercenaryOffer>> QuoteAsync(ClashOfRimClientNetworkContext context,
        ClientMercenarySelection selection, long ticks, CancellationToken token)
    {
        if (selection.IsGuard)
            return Map(await QuoteGuardAsync(context, ticks, selection.GuardTier!, token),
                r => new ClientMercenaryOffer(r.Result, r.BankStatus, r.PriceSilver, Tier: r.Tier, PointRatio: r.PointRatio));
        return Map(await QuoteAsync(context, ticks, selection.Skill, selection.Level, selection.Days, token),
            r => new ClientMercenaryOffer(r.Result, r.BankStatus, r.PriceSilver, r.HarmfulSurgeryFineSilver, r.DeathFineSilver));
    }
    public async Task<ClashOfRimClientNetworkResult<ClientMercenaryHireReply>> SubmitAsync(ClashOfRimClientNetworkContext context,
        ClientMercenaryCommand command, ModSnapshotPackageMetadataDto package, byte[] payload, CancellationToken token)
    {
        using var http = new HttpClient();
        var protocol = Create(http, context);
        if (command.Selection.IsGuard)
            return Map(await protocol.HireMercenaryGuardWithSnapshotAsync(command.Key, command.GameTicks, command.GuardContract(), package, payload, token),
                r => new ClientMercenaryHireReply(r.Result, r.BankStatus, r.Contract?.ContractId, r.AppliedSnapshotId, r.NextLineageToken));
        return Map(await protocol.HireMercenaryWithSnapshotAsync(command.Key, command.GameTicks, command.Contract(), package, payload, token),
            r => new ClientMercenaryHireReply(r.Result, r.BankStatus, r.Contract?.ContractId, r.AppliedSnapshotId, r.NextLineageToken));
    }
    private static ClashOfRimClientNetworkResult<TOut> Map<TIn, TOut>(ClashOfRimClientNetworkResult<TIn> result, Func<TIn, TOut> convert) =>
        result.Success && result.Response is not null ? ClashOfRimClientNetworkResult<TOut>.Ok(convert(result.Response))
            : ClashOfRimClientNetworkResult<TOut>.Failed(result.ErrorCode ?? "", result.Message ?? "");

    private static ClientMercenaryProtocol Create(HttpClient http, ClashOfRimClientNetworkContext context) => new(
        new ClientProtocolTransport(http, context, new ClientProtocolEnvironment()), context,
        () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
}
