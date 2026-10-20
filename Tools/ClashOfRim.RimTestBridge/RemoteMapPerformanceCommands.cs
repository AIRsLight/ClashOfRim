using RimTest.Runtime;

namespace ClashOfRim.RimTestBridge;

internal sealed class RemoteMapPerformanceCommands
{
    internal RemoteMapPerformanceCommands(CommandRouter router, BridgeActionPolicy policy)
    {
        router.Register("clash.remote.performance.start", "FIXTURE: begin bounded update/repaint and remote-load timing probes in the isolated game.", "fixture", a =>
        {
            policy.RequireFixture(a);
            RemoteMapPerformanceObservation.Start();
            return new { started = true };
        });
        router.Register("clash.remote.performance.stop", "FIXTURE: stop timing probes and return frame gaps, load spans and map size.", "fixture", a =>
        {
            policy.RequireFixture(a);
            return RemoteMapPerformanceObservation.Stop();
        });
    }
}
