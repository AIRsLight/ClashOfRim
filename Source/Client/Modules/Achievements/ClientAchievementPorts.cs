using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Achievements;

internal sealed record ClientAchievementTarget(string UserId, string ColonyId)
{
    internal static ClientAchievementTarget Resolve(ClashOfRimClientNetworkContext context, string? user, string? colony)
        => new(string.IsNullOrWhiteSpace(user) ? context.UserId : user.Trim(),
            string.IsNullOrWhiteSpace(colony) ? context.ColonyId : colony.Trim());
}

internal interface IClientAchievementTransport
{
    Task<ClashOfRimClientNetworkResult<ModListAchievementsResponseDto>> ListAsync(
        ClashOfRimClientNetworkContext context, ClientAchievementTarget target, CancellationToken token);
}

internal interface IClientAchievementPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    void Diagnose(Exception exception);
}
