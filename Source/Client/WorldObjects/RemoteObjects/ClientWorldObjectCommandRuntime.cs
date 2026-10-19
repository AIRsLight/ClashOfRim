using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientWorldObjectCommandRuntime : IClientWorldObjectCommandRuntime
{
    public bool HasActiveRemoteMap => ClashOfRimGameComponent.HasActiveRemoteMapSession || RemoteSessionMapUtility.FindActiveSessionMap() is not null;
    public void Reject(string key) => Messages.Message(ClashOfRimText.Key(key), MessageTypeDefOf.RejectInput, historical: false);
}

internal sealed class ClientWorldObjectCommandTarget : IClientWorldObjectCommandTarget
{
    private readonly IRemoteWorldObjectView view;
    private readonly World? world = Find.World;
    internal ClientWorldObjectCommandTarget(IRemoteWorldObjectView view) => this.view = view;
    public ClientWorldObjectTargetState? Read()
    {
        var worldObject = view.WorldObject;
        if (!ReferenceEquals(world, Find.World) || worldObject.Destroyed || Find.WorldObjects?.AllWorldObjects.Contains(worldObject) != true) return null;
        var marker = RemoteWorldObjectUiUtility.CaptureTarget(view);
        // Registry projection already binds the proxy faction. Reading a gizmo
        // must not create or normalize another faction in the world.
        bool allied = marker.CanReinforce || marker.RelationKind == "Ally"
            || worldObject.Faction is { } faction && Faction.OfPlayer is { } player && faction.RelationKindWith(player) == FactionRelationKind.Ally;
        return new(marker, allied);
    }
}
