using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModCreateRaidRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "attacker")]
    public ModProtocolIdentityDto? Attacker { get; set; }

    [DataMember(Name = "defender")]
    public ModProtocolIdentityDto? Defender { get; set; }

    [DataMember(Name = "isHostile")]
    public bool IsHostile { get; set; }

    [DataMember(Name = "defenderOnline")]
    public bool DefenderOnline { get; set; }

    [DataMember(Name = "defenderWealth")]
    public int DefenderWealth { get; set; }

    [DataMember(Name = "defenderRaidCooldownUntilUtc")]
    public string? DefenderRaidCooldownUntilUtc { get; set; }

    [DataMember(Name = "raidPreparationId")]
    public string? RaidPreparationId { get; set; }

    [DataMember(Name = "targetWorldObjectId")]
    public string TargetWorldObjectId { get; set; } = string.Empty;

    [DataMember(Name = "targetMapId")]
    public string TargetMapId { get; set; } = string.Empty;

    [DataMember(Name = "targetTile")]
    public int? TargetTile { get; set; }

    [DataMember(Name = "defenderSnapshotId")]
    public string DefenderSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "opponentKind")]
    public string OpponentKind { get; set; } = "Player";

    [DataMember(Name = "pawnGlobalKeys")]
    public List<string> PawnGlobalKeys { get; set; } = new();

    [DataMember(Name = "carriedThings")]
    public List<ModThingReferenceDto> CarriedThings { get; set; } = new();

    [DataMember(Name = "guardDeploymentId")]
    public string? GuardDeploymentId { get; set; }
}

[DataContract]
public sealed class ModCreateRaidWithSnapshotRequestDto
{
    [DataMember(Name = "raid")]
    public ModCreateRaidRequestDto? Raid { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}

[DataContract]
public sealed class ModPrepareRaidRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "attacker")]
    public ModProtocolIdentityDto? Attacker { get; set; }

    [DataMember(Name = "defender")]
    public ModProtocolIdentityDto? Defender { get; set; }

    [DataMember(Name = "isHostile")]
    public bool IsHostile { get; set; }

    [DataMember(Name = "targetWorldObjectId")]
    public string TargetWorldObjectId { get; set; } = string.Empty;

    [DataMember(Name = "targetMapId")]
    public string TargetMapId { get; set; } = string.Empty;

    [DataMember(Name = "targetTile")]
    public int? TargetTile { get; set; }

    [DataMember(Name = "opponentKind")]
    public string OpponentKind { get; set; } = "Player";
}

[DataContract]
public sealed class ModPrepareRaidResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "raidEventId")]
    public string? RaidEventId { get; set; }

    [DataMember(Name = "raidPreparationId")]
    public string? RaidPreparationId { get; set; }

    [DataMember(Name = "defenderSnapshotId")]
    public string? DefenderSnapshotId { get; set; }

    [DataMember(Name = "defenderPackage")]
    public ModSnapshotPackageMetadataDto? DefenderPackage { get; set; }

    [DataMember(Name = "expiresAtUtc")]
    public string? ExpiresAtUtc { get; set; }

    [DataMember(Name = "raidMaxDurationMinutes")]
    public double? RaidMaxDurationMinutes { get; set; }

    [DataMember(Name = "raidTimeoutGraceMinutes")]
    public double? RaidTimeoutGraceMinutes { get; set; }

    [DataMember(Name = "guardDeployment")]
    public ModRaidGuardDeploymentDto? GuardDeployment { get; set; }
}

[DataContract]
public sealed class ModRaidGuardDeploymentDto
{
    [DataMember(Name = "contractId")]
    public string ContractId { get; set; } = string.Empty;

    [DataMember(Name = "tier")]
    public string Tier { get; set; } = string.Empty;

    [DataMember(Name = "priceSilver")]
    public int PriceSilver { get; set; }

    [DataMember(Name = "pointRatio")]
    public float PointRatio { get; set; }

    [DataMember(Name = "points")]
    public int Points { get; set; }

    [DataMember(Name = "seed")]
    public int Seed { get; set; }
}

[DataContract]
public sealed class ModActiveRaidRecoveryDto
{
    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "status")]
    public string Status { get; set; } = string.Empty;

    [DataMember(Name = "serverNowUtc")]
    public string ServerNowUtc { get; set; } = string.Empty;

    [DataMember(Name = "startedAtUtc")]
    public string StartedAtUtc { get; set; } = string.Empty;

    [DataMember(Name = "deadlineUtc")]
    public string DeadlineUtc { get; set; } = string.Empty;

    [DataMember(Name = "finalDeadlineUtc")]
    public string FinalDeadlineUtc { get; set; } = string.Empty;

    [DataMember(Name = "defenderUserId")]
    public string DefenderUserId { get; set; } = string.Empty;

    [DataMember(Name = "defenderColonyId")]
    public string? DefenderColonyId { get; set; }

    [DataMember(Name = "defenderSnapshotId")]
    public string DefenderSnapshotId { get; set; } = string.Empty;
}
