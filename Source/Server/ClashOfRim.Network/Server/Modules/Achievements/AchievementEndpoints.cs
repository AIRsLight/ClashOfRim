using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Achievements;

internal static class AchievementEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, AchievementQueries achievements)
        => endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListAchievements).Route,
            (ListAchievementsRequest request) => Results.Ok(achievements.List(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
}
