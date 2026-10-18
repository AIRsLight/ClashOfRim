using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidPreparation(IColonySnapshotIndexStore snapshots, RaidPreparationRegistry preparations,
    MercenaryGuardContractRegistry guards, RaidQueries queries, RaidAdmission admission,
    IRaidReconciliation reconciliation, WorldConfigurationRegistry world, Func<ClashOfRimServerConfiguration> configuration)
{
    internal PrepareRaidResponse Prepare(PrepareRaidRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        reconciliation.Reconcile(now);
        if (request.Attacker is null || string.IsNullOrWhiteSpace(request.Attacker.UserId)
            || string.IsNullOrWhiteSpace(request.Attacker.ColonyId) || string.IsNullOrWhiteSpace(request.Attacker.SnapshotId)
            || request.Defender is null || string.IsNullOrWhiteSpace(request.Defender.UserId)
            || string.IsNullOrWhiteSpace(request.Defender.ColonyId) || string.IsNullOrWhiteSpace(request.TargetWorldObjectId)
            || string.IsNullOrWhiteSpace(request.TargetMapId))
            return Reject(ProtocolErrorCode.ValidationFailed, "Raid.PrepareMissingFields");
        if (queries.UnsettledAttacker(request.Attacker.UserId, request.Attacker.ColonyId, request.IdempotencyKey) is { } blocking)
            return Reject(ProtocolErrorCode.ServerRejected, "Raid.BlockedByUnsettledAttackerRaid", blocking.EventId);
        if (RaidAdmission.OpponentKind(request.OpponentKind) != RaidOpponentKind.Player)
            return Reject(ProtocolErrorCode.ValidationFailed, "Raid.PreparePlayerOnly");
        if (!configuration().PvpEnabled) return Reject(ProtocolErrorCode.ServerRejected, "Pvp.Disabled");
        if (snapshots is not IColonySnapshotPackageStore packages)
            return Reject(ProtocolErrorCode.ServerRejected, "Raid.PrepareSnapshotStoreUnsupported");
        var defender = packages.GetLatestPackage(request.Defender.UserId, request.Defender.ColonyId);
        if (defender is null) return Reject(ProtocolErrorCode.SnapshotMismatch, "Raid.MissingDefenderSnapshot");
        var eligibility = admission.Describe(request.Attacker, request.Defender, defender.Envelope.Identity,
            defender.Index, request.TargetMapId, now);
        if (admission.Check(eligibility) is { } failure) return new(failure, null, null, null, null, null);

        string raidId = AuthoritativeEventFactory.BuildEventId(ServerEventType.Raid, request.IdempotencyKey);
        var preparation = preparations.Create(request.IdempotencyKey, raidId, request.Attacker.UserId, request.Attacker.ColonyId,
            request.Defender.UserId, request.Defender.ColonyId, defender.Envelope.Identity.SnapshotId ?? string.Empty,
            request.TargetWorldObjectId, request.TargetMapId, request.TargetTile, now, TimeSpan.FromMinutes(10));
        var config = configuration();
        return new(ProtocolResponse.Ok(ServerText.Format("Raid.PrepareCreated")), raidId, preparation.PreparationId,
            defender.Envelope.Identity.SnapshotId, ProtocolDtoMapper.ToMetadataDto(defender), preparation.ExpiresAtUtc,
            config.RaidMaxDuration.TotalMinutes, config.RaidTimeoutGracePeriod.TotalMinutes,
            GuardDeployment(request.Defender, raidId, defender, config));
    }

    private RaidGuardDeploymentDto? GuardDeployment(ProtocolIdentity defender, string raidId,
        SaveSnapshotPackage snapshot, ClashOfRimServerConfiguration config)
    {
        var guard = guards.FindActiveForColony(defender.UserId, defender.ColonyId!);
        if (guard is null) return null;
        int basePoints = CoreRaidDifficultyServerCompatibility.EstimateDefaultThreatPointsForSnapshot(snapshot,
            SnapshotWealth.Total(snapshot.Index) ?? 0, config.RaidMinimumDefenderWealth, world.Current?.Extensions);
        int points = Math.Max(35, (int)Math.Ceiling(basePoints * Math.Max(0f, guard.PointRatio)));
        return new(guard.ContractId, guard.Tier, guard.PriceSilver, guard.PointRatio, points, HashCode.Combine(guard.ContractId, raidId));
    }

    private static PrepareRaidResponse Reject(ProtocolErrorCode code, string key, string? raidId = null) =>
        new(ProtocolResponse.Reject(code, ServerText.Format(key)), raidId, null, null, null, null);
}
