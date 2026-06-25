using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim.Achievements;

internal sealed class AchievementTrophyData : IExposable
{
    public string AchievementId = string.Empty;
    public string Category = string.Empty;
    public string LabelKey = string.Empty;
    public string DescriptionKey = string.Empty;
    public string IconId = string.Empty;
    public string Color = string.Empty;
    public long Value;
    public string SourceSnapshotId = string.Empty;
    public string OwnerUserId = string.Empty;
    public string OwnerColonyId = string.Empty;
    public string OwnerDisplayName = string.Empty;

    public static AchievementTrophyData FromAchievement(
        ModAchievementSummaryDto achievement,
        string ownerUserId,
        string ownerColonyId,
        string ownerDisplayName)
    {
        return new AchievementTrophyData
        {
            AchievementId = achievement.AchievementId ?? string.Empty,
            Category = achievement.Category ?? string.Empty,
            LabelKey = achievement.LabelKey ?? string.Empty,
            DescriptionKey = achievement.DescriptionKey ?? string.Empty,
            IconId = achievement.IconId ?? string.Empty,
            Color = achievement.Color ?? string.Empty,
            Value = achievement.Value,
            SourceSnapshotId = achievement.SourceSnapshotId ?? string.Empty,
            OwnerUserId = ownerUserId ?? string.Empty,
            OwnerColonyId = ownerColonyId ?? string.Empty,
            OwnerDisplayName = string.IsNullOrWhiteSpace(ownerDisplayName) ? ownerUserId ?? string.Empty : ownerDisplayName
        };
    }

    public AchievementTrophyData Clone()
    {
        return new AchievementTrophyData
        {
            AchievementId = AchievementId,
            Category = Category,
            LabelKey = LabelKey,
            DescriptionKey = DescriptionKey,
            IconId = IconId,
            Color = Color,
            Value = Value,
            SourceSnapshotId = SourceSnapshotId,
            OwnerUserId = OwnerUserId,
            OwnerColonyId = OwnerColonyId,
            OwnerDisplayName = OwnerDisplayName
        };
    }

    public string ResolvedLabel()
    {
        if (!string.IsNullOrWhiteSpace(LabelKey))
        {
            string translated = ClashOfRimText.Key(LabelKey);
            if (!string.Equals(translated, LabelKey, System.StringComparison.Ordinal))
            {
                return translated;
            }
        }

        return string.IsNullOrWhiteSpace(AchievementId)
            ? ClashOfRimText.Key("ClashOfRim.Achievement.Unknown")
            : AchievementId;
    }

    public string ResolvedDescription()
    {
        if (!string.IsNullOrWhiteSpace(DescriptionKey))
        {
            string translated = ClashOfRimText.Key(DescriptionKey);
            if (!string.Equals(translated, DescriptionKey, System.StringComparison.Ordinal))
            {
                return translated;
            }
        }

        return string.Empty;
    }

    public string ResolvedOwner()
    {
        return string.IsNullOrWhiteSpace(OwnerDisplayName)
            ? OwnerUserId
            : OwnerDisplayName;
    }

    public string ResolvedValue()
    {
        return Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    public string BuildInspectLine()
    {
        return ClashOfRimText.Key(
            "ClashOfRim.Achievement.TrophyInspectLine",
            ResolvedOwner().Named("OWNER"),
            ResolvedLabel().Named("ACHIEVEMENT"),
            ResolvedValue().Named("VALUE"));
    }

    public string BuildArtDescription()
    {
        return ClashOfRimText.Key(
            "ClashOfRim.Achievement.TrophyArtDescription",
            ResolvedOwner().Named("OWNER"),
            ResolvedLabel().Named("ACHIEVEMENT"),
            ResolvedDescription().Named("DESCRIPTION"),
            ResolvedValue().Named("VALUE"));
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref AchievementId, "achievementId", string.Empty);
        Scribe_Values.Look(ref Category, "category", string.Empty);
        Scribe_Values.Look(ref LabelKey, "labelKey", string.Empty);
        Scribe_Values.Look(ref DescriptionKey, "descriptionKey", string.Empty);
        Scribe_Values.Look(ref IconId, "iconId", string.Empty);
        Scribe_Values.Look(ref Color, "color", string.Empty);
        Scribe_Values.Look(ref Value, "value", 0L);
        Scribe_Values.Look(ref SourceSnapshotId, "sourceSnapshotId", string.Empty);
        Scribe_Values.Look(ref OwnerUserId, "ownerUserId", string.Empty);
        Scribe_Values.Look(ref OwnerColonyId, "ownerColonyId", string.Empty);
        Scribe_Values.Look(ref OwnerDisplayName, "ownerDisplayName", string.Empty);
    }
}
