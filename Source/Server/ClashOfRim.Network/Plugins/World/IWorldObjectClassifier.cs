using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public interface IWorldObjectClassifier
{
    bool IsOrbitalWorldObject(WorldObjectSummary worldObject);

    bool IsPlayerColonyAnchor(WorldObjectSummary worldObject)
    {
        return false;
    }
}
