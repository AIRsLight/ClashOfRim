using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

internal interface IClientMercenaryQuoteTransport
{
    Task<ClashOfRimClientNetworkResult<ModMercenaryQuoteResponseDto>> QuoteAsync(ClashOfRimClientNetworkContext context,
        long ticks, string skill, int level, int days, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModMercenaryGuardQuoteResponseDto>> QuoteGuardAsync(ClashOfRimClientNetworkContext context,
        long ticks, string tier, CancellationToken token);
}

internal interface IClientMercenaryPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    void Diagnose(Exception exception);
}
