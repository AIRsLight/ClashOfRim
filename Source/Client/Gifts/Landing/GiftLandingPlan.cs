using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Gifts;

public sealed class GiftLandingPlan
{
    public GiftLandingPlan(
        string eventId,
        string? worldObjectId,
        string targetMapUniqueId,
        int? tile,
        string landingMode,
        IReadOnlyList<GiftItemReference> items,
        bool requiresSnapshotConfirmation,
        bool skipFailedItems = false,
        string? arrivalLetterLabel = null,
        string? arrivalLetterText = null)
    {
        EventId = eventId;
        WorldObjectId = worldObjectId;
        TargetMapUniqueId = targetMapUniqueId;
        Tile = tile;
        LandingMode = landingMode;
        Items = items;
        RequiresSnapshotConfirmation = requiresSnapshotConfirmation;
        SkipFailedItems = skipFailedItems;
        ArrivalLetterLabel = arrivalLetterLabel;
        ArrivalLetterText = arrivalLetterText;
    }

    public string EventId { get; }

    public string? WorldObjectId { get; }

    public string TargetMapUniqueId { get; }

    public int? Tile { get; }

    public string LandingMode { get; }

    public IReadOnlyList<GiftItemReference> Items { get; }

    public bool RequiresSnapshotConfirmation { get; }

    public bool SkipFailedItems { get; }

    public string? ArrivalLetterLabel { get; }

    public string? ArrivalLetterText { get; }
}
