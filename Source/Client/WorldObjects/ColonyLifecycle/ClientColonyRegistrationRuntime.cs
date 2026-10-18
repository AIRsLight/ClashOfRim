using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientColonyRegistrationRuntime : IClientColonyRegistrationRuntime
{
    public IClientColonyRegistrationSession Bind() => new Session(Current.Game, Find.World);
    private sealed record Session(Game? Game, World? World) : IClientColonyRegistrationSession
    {
        public bool IsCurrent => ReferenceEquals(Game, Current.Game) && ReferenceEquals(World, Find.World);
        public bool Playing => Current.ProgramState == ProgramState.Playing;
        public bool HasWorld => World is not null;
        public bool HasWorldObjects => World?.worldObjects is not null;
        public bool HasColonyContext => Game?.CurrentMap is not null || Game?.Maps?.Any(ClientWorldColonySitesRuntime.IsPlayerColonyMap) == true;
        public bool HasExtensionCollectors => ClashOfRimCompatibilityApi.HasWorldConfigurationExtensionCollector;
        public bool HasRemoteSession => ClashOfRimGameComponent.HasActiveRaidBattleSession || ClashOfRimGameComponent.HasActiveObservationSession;
        public IReadOnlyCollection<ModPlayerColonySiteDto> CaptureSites(string user, string colony, ModColonyAppearanceDto? appearance)
            => ClientWorldColonySitesRuntime.Capture(Game, World, user, colony, appearance);
        public IReadOnlyCollection<ModWorldConfigurationExtensionDto> CaptureExtensions(string user, string colony, string snapshot)
            => ClashOfRimCompatibilityApi.CollectCurrentWorldConfigurationExtensions(user, colony, snapshot).ToArray();
        public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.Enqueue(action, discard);
    }
}
