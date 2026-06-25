using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class ListAchievementsRequest
{
    public ListAchievementsRequest(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        string? targetUserId = null,
        string? targetColonyId = null)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        TargetUserId = targetUserId;
        TargetColonyId = targetColonyId;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? CurrentSnapshotId { get; }

    public string? TargetUserId { get; }

    public string? TargetColonyId { get; }
}

public sealed class ListAchievementsResponse
{
    public ListAchievementsResponse(
        ProtocolResponse result,
        IReadOnlyList<AchievementLeaderboardDto> leaderboards,
        IReadOnlyList<AchievementSummaryDto> ownAchievements)
    {
        Result = result;
        Leaderboards = leaderboards;
        OwnAchievements = ownAchievements;
    }

    public ProtocolResponse Result { get; }

    public IReadOnlyList<AchievementLeaderboardDto> Leaderboards { get; }

    public IReadOnlyList<AchievementSummaryDto> OwnAchievements { get; }
}

public sealed class AchievementSummaryDto
{
    public AchievementSummaryDto(
        string achievementId,
        string category,
        string labelKey,
        string descriptionKey,
        string? iconId,
        string? color,
        long value,
        string? sourceSnapshotId)
    {
        AchievementId = achievementId;
        Category = category;
        LabelKey = labelKey;
        DescriptionKey = descriptionKey;
        IconId = iconId;
        Color = AchievementColors.Normalize(color);
        Value = value;
        SourceSnapshotId = sourceSnapshotId;
    }

    public string AchievementId { get; }

    public string Category { get; }

    public string LabelKey { get; }

    public string DescriptionKey { get; }

    public string? IconId { get; }

    public string Color { get; }

    public long Value { get; }

    public string? SourceSnapshotId { get; }
}

public sealed class AchievementLeaderboardDto
{
    public AchievementLeaderboardDto(
        string achievementId,
        string category,
        string labelKey,
        string descriptionKey,
        string? iconId,
        string? color,
        IReadOnlyList<AchievementLeaderboardEntryDto> entries)
    {
        AchievementId = achievementId;
        Category = category;
        LabelKey = labelKey;
        DescriptionKey = descriptionKey;
        IconId = iconId;
        Color = AchievementColors.Normalize(color);
        Entries = entries;
    }

    public string AchievementId { get; }

    public string Category { get; }

    public string LabelKey { get; }

    public string DescriptionKey { get; }

    public string? IconId { get; }

    public string Color { get; }

    public IReadOnlyList<AchievementLeaderboardEntryDto> Entries { get; }
}

public sealed class AchievementLeaderboardEntryDto
{
    public AchievementLeaderboardEntryDto(
        string userId,
        string colonyId,
        string? displayName,
        long value,
        string? sourceSnapshotId)
    {
        UserId = userId;
        ColonyId = colonyId;
        DisplayName = displayName;
        Value = value;
        SourceSnapshotId = sourceSnapshotId;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? DisplayName { get; }

    public long Value { get; }

    public string? SourceSnapshotId { get; }
}

public static class AchievementColors
{
    public const string Green = "Green";
    public const string Blue = "Blue";
    public const string Purple = "Purple";
    public const string Red = "Red";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Blue, System.StringComparison.OrdinalIgnoreCase))
        {
            return Blue;
        }

        if (string.Equals(value, Purple, System.StringComparison.OrdinalIgnoreCase))
        {
            return Purple;
        }

        if (string.Equals(value, Red, System.StringComparison.OrdinalIgnoreCase))
        {
            return Red;
        }

        if (IsHexColor(value))
        {
            return value!.Trim();
        }

        return Green;
    }

    private static bool IsHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value!.Trim();
        if (trimmed.Length != 7 && trimmed.Length != 9)
        {
            return false;
        }

        if (trimmed[0] != '#')
        {
            return false;
        }

        for (int i = 1; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            bool hex =
                (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }
}
