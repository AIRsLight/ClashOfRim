using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidLaunchRuntime : IClientRaidLaunchRuntime
{
    private readonly ClientRaidAttackForce force;
    private ActiveRaidBattleSession? battle;
    internal ClientRaidLaunchRuntime(IRaidAttackForceSource source) => force = new(source);
    public bool TryCapture(ClashOfRimClientNetworkContext context, ModWorldMapMarkerDto target, out ClientRaidForceManifest? manifest, out string failure)
        => force.TryCapture(context, target, out manifest, out failure);
    public string? EntryFailure() => force.EntryFailure();
    public void EnqueueOpen(Action action, Action discarded) => ClientRemoteMapLongEvent.Enqueue(action, discarded);
    public string? Open(ClashOfRimClientNetworkContext context, ModWorldMapMarkerDto target, ClientPreparedRaid prepared)
    {
        var request = new RemoteSessionMapOpenRequest
        {
            Target = target, Mode = ClientRemoteMapModes.RaidBattle,
            SessionId = $"raidbattle:{context.UserId}:{prepared.EventId}:{DateTime.UtcNow.Ticks}",
            SnapshotId = prepared.Snapshot.SnapshotId, RelatedEventId = prepared.EventId,
            Package = prepared.Snapshot.Package, Payload = prepared.Snapshot.Payload,
            PreparedDocument = prepared.Snapshot.PreparedDocument,
            CloseExistingObservationSessions = true, FailureCleanupReason = "raid battle map generation failure"
        };
        if (!RemoteSessionMapUtility.TryOpen(request, out var result)) return result.FailureReason;
        battle = ClientRemoteSessionRuntime.CreateRaidBattleSession(prepared.EventId, context.UserId, context.ColonyId,
            context.CurrentSnapshotId ?? string.Empty, request, result, prepared.Snapshot.Package.NextLineageToken ?? string.Empty,
            prepared.BattleDuration, prepared.SettlementGraceDuration, prepared.Guards);
        if (force.TryEnterBattle(battle, result.Map, out var failure)) return null;
        ClientRemoteSessionRuntime.CloseRaidBattleForExternalResolution(battle.EventId, "raid attack pawn transfer failed");
        return failure;
    }
    public bool HasBattle => battle is not null && ReferenceEquals(ClashOfRimGameComponent.ActiveRaidBattleSession, battle)
        && RemoteSessionMapUtility.FindActiveSessionMap(ActiveRemoteMapSession.FromRaidBattle(battle))?.Map is not null;
    public void ApplyReceipt(string snapshotId, ClientRaidServerDeadlines? deadlines)
    {
        battle!.AttackerSnapshotId = snapshotId;
        if (deadlines is null) return;
        ClientRaidBattleDeadlines.Apply(battle, deadlines);
    }
}
