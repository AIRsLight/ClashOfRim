using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldEntryStore : IClientWorldEntryStore, IClientWorldConfigurationStore, IClientOccupiedColonySiteIdentity
{
    private readonly ClashOfRimSettings settings;
    internal ClientWorldEntryStore(ClashOfRimSettings settings) => this.settings = settings;
    public string WorldId => settings.CurrentWorldConfigurationId;
    public void Initialize(string server, string user, string password)
    {
        var previous = (settings.ServerBaseUrl, settings.UserId, settings.ColonyId, settings.OfflinePassword,
            settings.CurrentSnapshotId, settings.CurrentLineageToken);
        settings.ServerBaseUrl = server; settings.UserId = user; settings.ColonyId = ""; settings.OfflinePassword = password;
        settings.CurrentSnapshotId = ""; settings.CurrentLineageToken = "";
        Save(() => (settings.ServerBaseUrl, settings.UserId, settings.ColonyId, settings.OfflinePassword,
            settings.CurrentSnapshotId, settings.CurrentLineageToken) = previous);
    }
    public void Publish(string colonyId, string snapshotId)
    {
        if (settings.ColonyId == colonyId && settings.CurrentSnapshotId == snapshotId) return;
        string colony = settings.ColonyId, snapshot = settings.CurrentSnapshotId;
        settings.ColonyId = colonyId; settings.CurrentSnapshotId = snapshotId;
        Save(() => { settings.ColonyId = colony; settings.CurrentSnapshotId = snapshot; });
    }
    public void Remember(string worldId)
    {
        if (settings.CurrentWorldConfigurationId == worldId) return;
        string previous = settings.CurrentWorldConfigurationId;
        settings.CurrentWorldConfigurationId = worldId;
        Save(() => settings.CurrentWorldConfigurationId = previous);
    }
    private void Save(System.Action rollback)
    {
        try { settings.Write(); }
        catch { rollback(); throw; }
    }
}
