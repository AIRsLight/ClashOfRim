using System;
using System.Collections.Generic;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class ActiveRaidBattleSession : IExposable
{
    public string EventId = string.Empty;
    public string AttackerUserId = string.Empty;
    public string AttackerColonyId = string.Empty;
    public string AttackerSnapshotId = string.Empty;
    public string DefenderUserId = string.Empty;
    public string DefenderColonyId = string.Empty;
    public string DefenderSnapshotId = string.Empty;
    public string DefenderLineageToken = string.Empty;
    public string TargetWorldObjectId = string.Empty;
    public string TargetMapId = string.Empty;
    public string ClientMapId = string.Empty;
    public int TargetTile = -1;
    public string SavePath = string.Empty;
    public List<string> AttackPawnThingIds = new();
    public long StartedAtUtcTicks;
    public long DeadlineUtcTicks;
    public long FinalDeadlineUtcTicks;
    public bool FinishInProgress;
    public string GuardDeploymentId = string.Empty;
    public string GuardDeploymentTier = string.Empty;
    public int GuardDeploymentPoints;
    public int GuardDeploymentSeed;

    public DateTime StartedAtUtc =>
        StartedAtUtcTicks <= 0 ? DateTime.UtcNow : new DateTime(StartedAtUtcTicks, DateTimeKind.Utc);

    public DateTime DeadlineUtc =>
        DeadlineUtcTicks <= 0 ? DateTime.UtcNow : new DateTime(DeadlineUtcTicks, DateTimeKind.Utc);

    public DateTime FinalDeadlineUtc =>
        FinalDeadlineUtcTicks <= 0 ? DeadlineUtc : new DateTime(FinalDeadlineUtcTicks, DateTimeKind.Utc);

    public TimeSpan Remaining => DeadlineUtc - DateTime.UtcNow;

    public TimeSpan FinalRemaining => FinalDeadlineUtc - DateTime.UtcNow;

    public bool IsExpired => Remaining <= TimeSpan.Zero;

    public bool IsFinalDeadlineExpired => FinalRemaining <= TimeSpan.Zero;

    public void ExposeData()
    {
        Scribe_Values.Look(ref EventId, "eventId", string.Empty);
        Scribe_Values.Look(ref AttackerUserId, "attackerUserId", string.Empty);
        Scribe_Values.Look(ref AttackerColonyId, "attackerColonyId", string.Empty);
        Scribe_Values.Look(ref AttackerSnapshotId, "attackerSnapshotId", string.Empty);
        Scribe_Values.Look(ref DefenderUserId, "defenderUserId", string.Empty);
        Scribe_Values.Look(ref DefenderColonyId, "defenderColonyId", string.Empty);
        Scribe_Values.Look(ref DefenderSnapshotId, "defenderSnapshotId", string.Empty);
        Scribe_Values.Look(ref DefenderLineageToken, "defenderLineageToken", string.Empty);
        Scribe_Values.Look(ref TargetWorldObjectId, "targetWorldObjectId", string.Empty);
        Scribe_Values.Look(ref TargetMapId, "targetMapId", string.Empty);
        Scribe_Values.Look(ref ClientMapId, "clientMapId", string.Empty);
        Scribe_Values.Look(ref TargetTile, "targetTile", -1);
        Scribe_Values.Look(ref SavePath, "savePath", string.Empty);
        Scribe_Collections.Look(ref AttackPawnThingIds, "attackPawnThingIds", LookMode.Value);
        Scribe_Values.Look(ref StartedAtUtcTicks, "startedAtUtcTicks", 0L);
        Scribe_Values.Look(ref DeadlineUtcTicks, "deadlineUtcTicks", 0L);
        Scribe_Values.Look(ref FinalDeadlineUtcTicks, "finalDeadlineUtcTicks", 0L);
        Scribe_Values.Look(ref FinishInProgress, "finishInProgress", false);
        Scribe_Values.Look(ref GuardDeploymentId, "guardDeploymentId", string.Empty);
        Scribe_Values.Look(ref GuardDeploymentTier, "guardDeploymentTier", string.Empty);
        Scribe_Values.Look(ref GuardDeploymentPoints, "guardDeploymentPoints", 0);
        Scribe_Values.Look(ref GuardDeploymentSeed, "guardDeploymentSeed", 0);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            AttackPawnThingIds ??= new List<string>();
            DefenderLineageToken ??= string.Empty;
            ClientMapId ??= string.Empty;
            GuardDeploymentId ??= string.Empty;
            GuardDeploymentTier ??= string.Empty;
            FinishInProgress = false;
        }
    }
}
