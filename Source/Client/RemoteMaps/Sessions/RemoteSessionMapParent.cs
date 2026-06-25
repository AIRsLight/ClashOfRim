using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RemoteSessionMapParent : MapParent
{
    public const string ScoutMode = "Scout";
    public const string FriendlyObservationMode = "FriendlyObservation";
    public const string RaidObservationMode = "RaidObservation";
    public const string RaidBattleMode = "RaidBattle";
    public const string RaidTargetDownloadMode = "RaidTarget";
    public const string RaidPreparationDownloadMode = "RaidPreparation";

    public string SessionId = string.Empty;
    public string Mode = string.Empty;
    public string OwnerUserId = string.Empty;
    public string OwnerColonyId = string.Empty;
    public string SourceWorldObjectId = string.Empty;
    public string SourceMapId = string.Empty;
    public string SourceSnapshotId = string.Empty;
    public string RelatedEventId = string.Empty;
    public string SourceLabel = string.Empty;
    private List<RemoteNpcLordSnapshot> remoteNpcLordSnapshots = new();

    public override string Label => string.IsNullOrWhiteSpace(SourceLabel)
        ? ClashOfRimText.Key(LabelKey, OwnerUserId.Named("OWNER"))
        : ClashOfRimText.Key(
            NamedLabelKey,
            OwnerUserId.Named("OWNER"),
            SourceLabel.Named("LABEL"));

    public override bool CanReformFoggedEnemies => IsRaidBattle || base.CanReformFoggedEnemies;

    protected override bool UseGenericEnterMapFloatMenuOption => IsRaidBattle;

    public override void ExposeData()
    {
        base.ExposeData();
#pragma warning disable CS8601
        Scribe_Values.Look(ref SessionId, "clashOfRimSessionId");
        Scribe_Values.Look(ref Mode, "clashOfRimMode");
        Scribe_Values.Look(ref OwnerUserId, "clashOfRimOwnerUserId");
        Scribe_Values.Look(ref OwnerColonyId, "clashOfRimOwnerColonyId");
        Scribe_Values.Look(ref SourceWorldObjectId, "clashOfRimSourceWorldObjectId");
        Scribe_Values.Look(ref SourceMapId, "clashOfRimSourceMapId");
        Scribe_Values.Look(ref SourceSnapshotId, "clashOfRimSourceSnapshotId");
        Scribe_Values.Look(ref RelatedEventId, "clashOfRimRelatedEventId");
        Scribe_Values.Look(ref SourceLabel, "clashOfRimSourceLabel");
#pragma warning restore CS8601
        SessionId ??= string.Empty;
        Mode ??= string.Empty;
        OwnerUserId ??= string.Empty;
        OwnerColonyId ??= string.Empty;
        SourceWorldObjectId ??= string.Empty;
        SourceMapId ??= string.Empty;
        SourceSnapshotId ??= string.Empty;
        RelatedEventId ??= string.Empty;
        SourceLabel ??= string.Empty;
    }

    public void Configure(
        string sessionId,
        string mode,
        string ownerUserId,
        string? ownerColonyId,
        string? worldObjectId,
        string? mapId,
        string? snapshotId,
        string? relatedEventId,
        string? label)
    {
        SessionId = sessionId;
        Mode = mode;
        OwnerUserId = ownerUserId;
        OwnerColonyId = ownerColonyId ?? string.Empty;
        SourceWorldObjectId = worldObjectId ?? string.Empty;
        SourceMapId = mapId ?? string.Empty;
        SourceSnapshotId = snapshotId ?? string.Empty;
        RelatedEventId = relatedEventId ?? string.Empty;
        SourceLabel = label ?? string.Empty;
    }

    public override IEnumerable<Gizmo> GetGizmos()
    {
        if (IsRaidBattle)
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            yield break;
        }

        yield return new Command_Action
        {
            defaultLabel = ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.Close"),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.CloseDesc"),
            icon = TexButton.CloseXSmall,
            action = () => RemoteMapSessionController.CloseObservation(this)
        };
    }

    public override IEnumerable<Gizmo> GetCaravanGizmos(Caravan caravan)
    {
        if (IsRaidBattle)
        {
            foreach (Gizmo gizmo in base.GetCaravanGizmos(caravan))
            {
                yield return gizmo;
            }
        }
    }

    public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
    {
        if (IsRaidBattle)
        {
            foreach (FloatMenuOption option in base.GetFloatMenuOptions(caravan))
            {
                yield return option;
            }
        }
    }

    public override IEnumerable<FloatMenuOption> GetTransportersFloatMenuOptions(
        IEnumerable<IThingHolder> pods,
        System.Action<PlanetTile, TransportersArrivalAction> launchAction)
    {
        if (Policy.CanUseTransportPods)
        {
            foreach (FloatMenuOption option in base.GetTransportersFloatMenuOptions(pods, launchAction))
            {
                yield return option;
            }
        }

        yield break;
    }

    public override IEnumerable<FloatMenuOption> GetShuttleFloatMenuOptions(
        IEnumerable<IThingHolder> pods,
        System.Action<PlanetTile, TransportersArrivalAction> launchAction)
    {
        if (Policy.CanUseTransportPods)
        {
            foreach (FloatMenuOption option in base.GetShuttleFloatMenuOptions(pods, launchAction))
            {
                yield return option;
            }
        }

        yield break;
    }

    public override string GetInspectString()
    {
        return ClashOfRimText.Key(
            "ClashOfRim.RemoteSessionMap.Inspect",
            OwnerUserId.Named("OWNER"),
            OwnerColonyId.Named("COLONY"),
            SourceSnapshotId.Named("SNAPSHOT"));
    }

    public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
    {
        alsoRemoveWorldObject = false;
        if (!IsRaidBattle || !HasMap)
        {
            return false;
        }

        if (Map.mapPawns.AnyPawnBlockingMapRemoval)
        {
            return false;
        }

        if (TransporterUtility.IncomingTransporterPreventingMapRemoval(Map))
        {
            return false;
        }

        alsoRemoveWorldObject = true;
        return true;
    }

    public bool IsRaidBattle => string.Equals(Mode, RaidBattleMode, System.StringComparison.OrdinalIgnoreCase);

    public RemoteMapSessionKind SessionKind => ActiveRemoteMapSession.KindFromMode(Mode);

    public RemoteMapSessionPolicy Policy => RemoteMapSessionPolicy.For(SessionKind);

    internal IReadOnlyList<RemoteNpcLordSnapshot> RemoteNpcLordSnapshots => remoteNpcLordSnapshots;

    internal void SetRemoteNpcLordSnapshots(IEnumerable<RemoteNpcLordSnapshot>? snapshots)
    {
        remoteNpcLordSnapshots = snapshots?.ToList() ?? new List<RemoteNpcLordSnapshot>();
    }

    private string LabelKey
    {
        get
        {
            if (IsRaidBattle)
            {
                return "ClashOfRim.RemoteSessionMap.RaidBattleLabel";
            }

            if (string.Equals(Mode, RaidObservationMode, System.StringComparison.OrdinalIgnoreCase))
            {
                return "ClashOfRim.RemoteSessionMap.RaidObservationLabel";
            }

            return string.Equals(Mode, FriendlyObservationMode, System.StringComparison.OrdinalIgnoreCase)
                ? "ClashOfRim.RemoteSessionMap.FriendlyObservationLabel"
                : "ClashOfRim.RemoteSessionMap.ScoutLabel";
        }
    }

    private string NamedLabelKey
    {
        get
        {
            if (IsRaidBattle)
            {
                return "ClashOfRim.RemoteSessionMap.RaidBattleNamedLabel";
            }

            if (string.Equals(Mode, RaidObservationMode, System.StringComparison.OrdinalIgnoreCase))
            {
                return "ClashOfRim.RemoteSessionMap.RaidObservationNamedLabel";
            }

            return string.Equals(Mode, FriendlyObservationMode, System.StringComparison.OrdinalIgnoreCase)
                ? "ClashOfRim.RemoteSessionMap.FriendlyObservationNamedLabel"
                : "ClashOfRim.RemoteSessionMap.ScoutNamedLabel";
        }
    }
}
