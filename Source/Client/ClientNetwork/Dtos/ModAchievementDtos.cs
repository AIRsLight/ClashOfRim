using System.Collections.Generic;
using System.Runtime.Serialization;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModListAchievementsRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string? CurrentSnapshotId { get; set; }

    [DataMember(Name = "targetUserId")]
    public string? TargetUserId { get; set; }

    [DataMember(Name = "targetColonyId")]
    public string? TargetColonyId { get; set; }
}

[DataContract]
public sealed class ModListAchievementsResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "leaderboards")]
    public List<ModAchievementLeaderboardDto> Leaderboards { get; set; } = new();

    [DataMember(Name = "ownAchievements")]
    public List<ModAchievementSummaryDto> OwnAchievements { get; set; } = new();
}

[DataContract]
public sealed class ModAchievementSummaryDto
{
    [DataMember(Name = "achievementId")]
    public string AchievementId { get; set; } = string.Empty;

    [DataMember(Name = "category")]
    public string Category { get; set; } = string.Empty;

    [DataMember(Name = "labelKey")]
    public string LabelKey { get; set; } = string.Empty;

    [DataMember(Name = "descriptionKey")]
    public string DescriptionKey { get; set; } = string.Empty;

    [DataMember(Name = "iconId")]
    public string? IconId { get; set; }

    [DataMember(Name = "color")]
    public string Color { get; set; } = "Green";

    [DataMember(Name = "value")]
    public long Value { get; set; }

    [DataMember(Name = "sourceSnapshotId")]
    public string? SourceSnapshotId { get; set; }
}

[DataContract]
public sealed class ModAchievementLeaderboardDto
{
    [DataMember(Name = "achievementId")]
    public string AchievementId { get; set; } = string.Empty;

    [DataMember(Name = "category")]
    public string Category { get; set; } = string.Empty;

    [DataMember(Name = "labelKey")]
    public string LabelKey { get; set; } = string.Empty;

    [DataMember(Name = "descriptionKey")]
    public string DescriptionKey { get; set; } = string.Empty;

    [DataMember(Name = "iconId")]
    public string? IconId { get; set; }

    [DataMember(Name = "color")]
    public string Color { get; set; } = "Green";

    [DataMember(Name = "entries")]
    public List<ModAchievementLeaderboardEntryDto> Entries { get; set; } = new();
}

[DataContract]
public sealed class ModAchievementLeaderboardEntryDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "displayName")]
    public string? DisplayName { get; set; }

    [DataMember(Name = "value")]
    public long Value { get; set; }

    [DataMember(Name = "sourceSnapshotId")]
    public string? SourceSnapshotId { get; set; }
}
