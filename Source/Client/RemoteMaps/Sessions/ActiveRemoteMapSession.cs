using System;
using AIRsLight.ClashOfRim.Raids;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class ActiveRemoteMapSession : IExposable
{
    public string SessionId = string.Empty;
    public RemoteMapSessionKind Kind;
    public string Mode = string.Empty;
    public string RelatedEventId = string.Empty;
    public string TargetUserId = string.Empty;
    public string TargetColonyId = string.Empty;
    public string TargetSnapshotId = string.Empty;
    public string TargetWorldObjectId = string.Empty;
    public string TargetMapId = string.Empty;
    public string ClientMapId = string.Empty;
    public int TargetTile = -1;
    public long StartedAtUtcTicks;

    public bool IsActive => Kind != RemoteMapSessionKind.None && !string.IsNullOrWhiteSpace(SessionId);

    public bool IsRaidBattle => Kind == RemoteMapSessionKind.RaidBattle;

    public bool IsObservation => Kind is RemoteMapSessionKind.ScoutEnemy
        or RemoteMapSessionKind.ObserveAlly
        or RemoteMapSessionKind.ObserveRaid;

    public DateTime StartedAtUtc =>
        StartedAtUtcTicks <= 0 ? DateTime.UtcNow : new DateTime(StartedAtUtcTicks, DateTimeKind.Utc);

    public void ExposeData()
    {
        Scribe_Values.Look(ref SessionId, "sessionId", string.Empty);
        Scribe_Values.Look(ref Kind, "kind", RemoteMapSessionKind.None);
        Scribe_Values.Look(ref Mode, "mode", string.Empty);
        Scribe_Values.Look(ref RelatedEventId, "relatedEventId", string.Empty);
        Scribe_Values.Look(ref TargetUserId, "targetUserId", string.Empty);
        Scribe_Values.Look(ref TargetColonyId, "targetColonyId", string.Empty);
        Scribe_Values.Look(ref TargetSnapshotId, "targetSnapshotId", string.Empty);
        Scribe_Values.Look(ref TargetWorldObjectId, "targetWorldObjectId", string.Empty);
        Scribe_Values.Look(ref TargetMapId, "targetMapId", string.Empty);
        Scribe_Values.Look(ref ClientMapId, "clientMapId", string.Empty);
        Scribe_Values.Look(ref TargetTile, "targetTile", -1);
        Scribe_Values.Look(ref StartedAtUtcTicks, "startedAtUtcTicks", 0L);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            SessionId ??= string.Empty;
            Mode ??= string.Empty;
            RelatedEventId ??= string.Empty;
            TargetUserId ??= string.Empty;
            TargetColonyId ??= string.Empty;
            TargetSnapshotId ??= string.Empty;
            TargetWorldObjectId ??= string.Empty;
            TargetMapId ??= string.Empty;
            ClientMapId ??= string.Empty;
        }
    }

    public static ActiveRemoteMapSession FromObservation(
        string sessionId,
        string mode,
        string targetUserId,
        string targetColonyId,
        string targetSnapshotId,
        string? relatedEventId = null)
    {
        return new ActiveRemoteMapSession
        {
            SessionId = sessionId ?? string.Empty,
            Kind = KindFromMode(mode),
            Mode = mode ?? string.Empty,
            RelatedEventId = relatedEventId ?? string.Empty,
            TargetUserId = targetUserId ?? string.Empty,
            TargetColonyId = targetColonyId ?? string.Empty,
            TargetSnapshotId = targetSnapshotId ?? string.Empty,
            StartedAtUtcTicks = DateTime.UtcNow.Ticks
        };
    }

    public static ActiveRemoteMapSession FromOpenedObservation(
        RemoteSessionMapOpenRequest request,
        RemoteSessionMapOpenResult result)
    {
        return new ActiveRemoteMapSession
        {
            SessionId = request.SessionId ?? string.Empty,
            Kind = KindFromMode(request.Mode),
            Mode = request.Mode ?? string.Empty,
            RelatedEventId = request.RelatedEventId ?? string.Empty,
            TargetUserId = request.Target.OwnerUserId ?? string.Empty,
            TargetColonyId = request.Target.OwnerColonyId ?? string.Empty,
            TargetSnapshotId = request.SnapshotId ?? string.Empty,
            TargetWorldObjectId = request.Target.WorldObjectId ?? string.Empty,
            TargetMapId = request.Target.MapId ?? string.Empty,
            ClientMapId = result.Map is null ? string.Empty : "Map_" + result.Map.uniqueID,
            TargetTile = request.Target.Tile,
            StartedAtUtcTicks = DateTime.UtcNow.Ticks
        };
    }

    public static ActiveRemoteMapSession FromRaidBattle(ActiveRaidBattleSession session)
    {
        return new ActiveRemoteMapSession
        {
            SessionId = session.EventId,
            Kind = RemoteMapSessionKind.RaidBattle,
            Mode = RemoteSessionMapParent.RaidBattleMode,
            RelatedEventId = session.EventId,
            TargetUserId = session.DefenderUserId,
            TargetColonyId = session.DefenderColonyId,
            TargetSnapshotId = session.DefenderSnapshotId,
            TargetWorldObjectId = session.TargetWorldObjectId,
            TargetMapId = session.TargetMapId,
            ClientMapId = session.ClientMapId,
            TargetTile = session.TargetTile,
            StartedAtUtcTicks = session.StartedAtUtcTicks
        };
    }

    public static RemoteMapSessionKind KindFromMode(string? mode)
    {
        if (string.Equals(mode, RemoteSessionMapParent.RaidBattleMode, StringComparison.OrdinalIgnoreCase))
        {
            return RemoteMapSessionKind.RaidBattle;
        }

        if (string.Equals(mode, RemoteSessionMapParent.RaidObservationMode, StringComparison.OrdinalIgnoreCase))
        {
            return RemoteMapSessionKind.ObserveRaid;
        }

        if (string.Equals(mode, RemoteSessionMapParent.FriendlyObservationMode, StringComparison.OrdinalIgnoreCase))
        {
            return RemoteMapSessionKind.ObserveAlly;
        }

        return string.Equals(mode, RemoteSessionMapParent.ScoutMode, StringComparison.OrdinalIgnoreCase)
            ? RemoteMapSessionKind.ScoutEnemy
            : RemoteMapSessionKind.None;
    }
}
