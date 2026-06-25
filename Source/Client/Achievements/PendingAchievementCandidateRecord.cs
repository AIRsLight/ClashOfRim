using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed class PendingAchievementCandidateRecord : IExposable
{
    public string AchievementId = string.Empty;
    public string EventKey = string.Empty;
    public long Value;
    public string Category = string.Empty;
    public string LabelKey = string.Empty;
    public string IconId = string.Empty;
    public string Color = string.Empty;
    public string AggregationKind = string.Empty;
    public string MetadataJson = string.Empty;

    public PendingAchievementCandidateRecord()
    {
    }

    public PendingAchievementCandidateRecord(ModSnapshotAchievementCandidateDto candidate)
    {
        AchievementId = candidate.AchievementId ?? string.Empty;
        EventKey = candidate.EventKey ?? string.Empty;
        Value = candidate.Value;
        Category = candidate.Category ?? string.Empty;
        LabelKey = candidate.LabelKey ?? string.Empty;
        IconId = candidate.IconId ?? string.Empty;
        Color = candidate.Color ?? string.Empty;
        AggregationKind = candidate.AggregationKind ?? string.Empty;
        MetadataJson = candidate.MetadataJson ?? string.Empty;
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(AchievementId)
        && !string.IsNullOrWhiteSpace(EventKey);

    public string StableKey => AchievementId.Trim() + ":" + EventKey.Trim();

    public ModSnapshotAchievementCandidateDto ToDto()
    {
        return new ModSnapshotAchievementCandidateDto
        {
            AchievementId = AchievementId ?? string.Empty,
            EventKey = EventKey ?? string.Empty,
            Value = Value,
            Category = string.IsNullOrWhiteSpace(Category) ? null : Category,
            LabelKey = string.IsNullOrWhiteSpace(LabelKey) ? null : LabelKey,
            IconId = string.IsNullOrWhiteSpace(IconId) ? null : IconId,
            Color = string.IsNullOrWhiteSpace(Color) ? null : Color,
            AggregationKind = string.IsNullOrWhiteSpace(AggregationKind) ? null : AggregationKind,
            MetadataJson = string.IsNullOrWhiteSpace(MetadataJson) ? null : MetadataJson
        };
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref AchievementId, "achievementId", string.Empty);
        Scribe_Values.Look(ref EventKey, "eventKey", string.Empty);
        Scribe_Values.Look(ref Value, "value");
        Scribe_Values.Look(ref Category, "category", string.Empty);
        Scribe_Values.Look(ref LabelKey, "labelKey", string.Empty);
        Scribe_Values.Look(ref IconId, "iconId", string.Empty);
        Scribe_Values.Look(ref Color, "color", string.Empty);
        Scribe_Values.Look(ref AggregationKind, "aggregationKind", string.Empty);
        Scribe_Values.Look(ref MetadataJson, "metadataJson", string.Empty);
    }
}
