using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimTest.Runtime;

namespace ClashOfRim.RimTestBridge;

internal sealed class RemoteMapCommands
{
    internal RemoteMapCommands(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy)
    {
        router.Register("clash.remote", "Read active observation/raid identity and object references.", "observe", _ =>
        {
            var session = ClashOfRimGameComponent.ActiveRemoteMapSession;
            return new
            {
                active = session?.IsActive == true,
                session = session is null ? null : new
                {
                    reference = objects.Add(session),
                    session.SessionId,
                    kind = session.Kind.ToString(),
                    session.Mode,
                    session.TargetUserId,
                    session.TargetSnapshotId,
                    session.TargetMapId,
                    session.ClientMapId,
                    session.RelatedEventId,
                    session.IsRaidBattle,
                    session.IsObservation
                },
                raid = ClashOfRimGameComponent.ActiveRaidBattleSession is { } raid ? objects.Add(raid) : null
            };
        });
        router.Register("clash.remote.close", "Original remote-map primary action, with raid confirmation enabled.", "semantic", _ =>
        {
            policy.RequireLoopback();
            return new { invoked = RemoteMapSessionController.TryRunPrimaryAction(ClashOfRimGameComponent.ActiveRemoteMapSession, "RimTest player finish", true) };
        });
    }
}
