using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartRefreshAchievements(string? targetUserId = null, string? targetColonyId = null)
    {
        if (!settings.IsConfigured)
        {
            achievementStatus = ClashOfRimText.Key("ClashOfRim.Achievement.StatusNotConfigured");
            return;
        }

        string requestedTargetUserId = string.IsNullOrWhiteSpace(targetUserId)
            ? settings.UserId
            : targetUserId!.Trim();
        string requestedTargetColonyId = string.IsNullOrWhiteSpace(targetColonyId)
            ? settings.ColonyId
            : targetColonyId!.Trim();
        achievementStatus = ClashOfRimText.Key("ClashOfRim.Achievement.StatusRefreshing");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModListAchievementsResponseDto> result =
                    await client.ListAchievementsAsync(requestedTargetUserId, requestedTargetColonyId);
                if (!result.Success || result.Response is null)
                {
                    achievementStatus = ClashOfRimText.Key(
                        "ClashOfRim.Achievement.StatusRefreshFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    achievementStatus = ClashOfRimText.Key(
                        "ClashOfRim.Achievement.StatusRefreshRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        (result.Response.Result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                lock (eventStateLock)
                {
                    lastAchievementLeaderboards.Clear();
                    lastAchievementLeaderboards.AddRange(result.Response.Leaderboards ?? new List<ModAchievementLeaderboardDto>());
                    lastOwnAchievements.Clear();
                    lastOwnAchievements.AddRange(result.Response.OwnAchievements ?? new List<ModAchievementSummaryDto>());
                    achievementTargetUserId = requestedTargetUserId;
                    achievementTargetColonyId = requestedTargetColonyId;
                    achievementLeaderboardsSnapshotVersion++;
                }

                achievementStatus = ClashOfRimText.Key("ClashOfRim.Achievement.StatusRefreshed");
            }
            catch (Exception ex)
            {
                achievementStatus = ClashOfRimText.Key(
                    "ClashOfRim.Achievement.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] List achievements failed: " + ex);
            }
        });
    }
}
