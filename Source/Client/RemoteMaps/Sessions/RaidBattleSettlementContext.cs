using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.Modules.Raids;

namespace AIRsLight.ClashOfRim.RemoteMaps;

// Retained public data contract; the settlement module owns runtime execution.
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
        ClientRaidSettlementIntent.Key(Session.AttackerUserId, Session.EventId, FinishReason);

    public string DefenderClientApplicationResult => ClientRaidSettlementIntent.Result(FinishReason);

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
