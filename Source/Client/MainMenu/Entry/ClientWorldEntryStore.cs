using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldEntryStore : IClientWorldEntryStore
{
    private readonly ClashOfRimSettings settings;
    internal ClientWorldEntryStore(ClashOfRimSettings settings) => this.settings = settings;
    public void Publish(string colonyId, string snapshotId)
    {
        if (settings.ColonyId == colonyId && settings.CurrentSnapshotId == snapshotId) return;
        string colony = settings.ColonyId, snapshot = settings.CurrentSnapshotId;
        settings.ColonyId = colonyId; settings.CurrentSnapshotId = snapshotId;
        try { settings.Write(); }
        catch { settings.ColonyId = colony; settings.CurrentSnapshotId = snapshot; throw; }
    }
}
