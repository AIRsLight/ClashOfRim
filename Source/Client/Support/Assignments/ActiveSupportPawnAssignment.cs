using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Quests;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

public sealed class ActiveSupportPawnAssignment : IExposable
{
    public string EventId = string.Empty;
    public string PawnGlobalKey = string.Empty;
    public string PawnThingId = string.Empty;
    public string PawnLabel = string.Empty;
    public string OwnerUserId = string.Empty;
    public string? OwnerColonyId;
    public string? OwnerSnapshotId;
    public string? OriginalFactionDefName;
    public string? OriginalFactionName;
    public int? SourceTile;
    public string? SourceCaravanLoadId;
    public Dictionary<string, string?> PawnReferenceMetadata = new(StringComparer.Ordinal);
    public bool PermanentSupport;
    public int? SupportDurationDays;
    public long? ExpiresAtGameTicks;
    public bool AutoReturnOnSettlement;
    public bool FinishInProgress;

    public void ExposeData()
    {
        Scribe_Values.Look(ref EventId, "eventId", string.Empty);
        Scribe_Values.Look(ref PawnGlobalKey, "pawnGlobalKey", string.Empty);
        Scribe_Values.Look(ref PawnThingId, "pawnThingId", string.Empty);
        Scribe_Values.Look(ref PawnLabel, "pawnLabel", string.Empty);
        Scribe_Values.Look(ref OwnerUserId, "ownerUserId", string.Empty);
        Scribe_Values.Look(ref OwnerColonyId, "ownerColonyId");
        Scribe_Values.Look(ref OwnerSnapshotId, "ownerSnapshotId");
        Scribe_Values.Look(ref OriginalFactionDefName, "originalFactionDefName");
        Scribe_Values.Look(ref OriginalFactionName, "originalFactionName");
        Scribe_Values.Look(ref SourceTile, "sourceTile");
        Scribe_Values.Look(ref SourceCaravanLoadId, "sourceCaravanLoadId");
        Scribe_Collections.Look(
            ref PawnReferenceMetadata,
            "pawnReferenceMetadata",
            LookMode.Value,
            LookMode.Value,
            ref pawnReferenceMetadataKeys,
            ref pawnReferenceMetadataValues);
        PawnReferenceMetadata ??= new Dictionary<string, string?>(StringComparer.Ordinal);
        Scribe_Values.Look(ref PermanentSupport, "permanentSupport");
        Scribe_Values.Look(ref SupportDurationDays, "supportDurationDays");
        Scribe_Values.Look(ref ExpiresAtGameTicks, "expiresAtGameTicks");
        Scribe_Values.Look(ref AutoReturnOnSettlement, "autoReturnOnSettlement");
        Scribe_Values.Look(ref FinishInProgress, "finishInProgress");
    }

    private List<string>? pawnReferenceMetadataKeys;
    private List<string?>? pawnReferenceMetadataValues;

    public bool IsExpired(long currentGameTicks)
    {
        return !PermanentSupport
            && ExpiresAtGameTicks.HasValue
            && ClashManagedQuestTimingUtility.IsExpiredAt(ExpiresAtGameTicks.Value, currentGameTicks);
    }

    public string InspectLine(long currentGameTicks)
    {
        if (PermanentSupport)
        {
            return ClashOfRimText.Key("ClashOfRim.Support.InspectPermanent");
        }

        if (!ExpiresAtGameTicks.HasValue)
        {
            return ClashOfRimText.Key("ClashOfRim.Support.InspectTemporary");
        }

        long remaining = Math.Max(0, ExpiresAtGameTicks.Value - currentGameTicks);
        float days = remaining / (float)ClashManagedQuestTimingUtility.TicksPerDay;
        return ClashOfRimText.Key(
            "ClashOfRim.Support.InspectTemporaryRemaining",
            days.ToString("0.#").Named("DAYS"));
    }
}
