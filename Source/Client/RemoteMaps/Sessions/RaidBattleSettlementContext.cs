using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Raids;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RaidBattleSettlementContext
{
    public RaidBattleSettlementContext(
        ActiveRaidBattleSession session,
        string finishReason,
        ModSnapshotPackageMetadataDto? confirmedSnapshot = null,
        byte[]? confirmedPayload = null)
    {
        Session = session;
        FinishReason = finishReason ?? string.Empty;
        ConfirmedSnapshot = confirmedSnapshot;
        ConfirmedPayload = confirmedPayload;
    }

    public ActiveRaidBattleSession Session { get; }

    public string FinishReason { get; }

    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; }

    public byte[]? ConfirmedPayload { get; }

    public string DefenderIdempotencyKey =>
        $"raid-settlement:{Session.AttackerUserId}:{Session.EventId}:{FinishReason}";

    public string DefenderClientApplicationResult => "RaidBattleReturned:" + FinishReason;

    public RaidBattleSettlementContext WithPreparedSnapshot(
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload)
    {
        return new RaidBattleSettlementContext(
            Session,
            FinishReason,
            confirmedSnapshot,
            confirmedPayload);
    }
}
