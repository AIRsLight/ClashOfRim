using RimTest.Runtime;

namespace ClashOfRim.RimTestBridge;

internal sealed class ThirdPartyMapCommands
{
    internal ThirdPartyMapCommands(CommandRouter router, BridgeActionPolicy policy)
    {
        router.Register("clash.thirdParty.map", "Observe native adaptive containers, vehicle passengers and active animations on one map.", "observe", a => ThirdPartyMapFixture.Read(BridgeMaps.Select(a)));
        router.Register("clash.fixture.thirdPartyMap", "FIXTURE: prepare native adaptive storage, vehicle crew and a registered melee duel for remote projection tests.", "fixture", a =>
        {
            policy.RequireFixture(a); var map = BridgeMaps.Select(a);
            ThirdPartyMapFixture.Storage(map); ThirdPartyMapFixture.Vehicle(map); ThirdPartyMapFixture.Animation(map);
            return new { fixture = true, state = ThirdPartyMapFixture.Read(map) };
        });
        router.Register("clash.fixture.stopThirdPartyAnimation", "FIXTURE: end the native test duel after projection verification, before unrelated caravan simulation.", "fixture", a =>
        {
            policy.RequireFixture(a); ThirdPartyMapFixture.StopAnimations(BridgeMaps.Select(a)); return new { fixture = true };
        });
    }
}
