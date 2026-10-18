using AIRsLight.ClashOfRim.Modules.Diplomacy;

namespace AIRsLight.ClashOfRim.Diplomacy;

// Settings remain the persisted compatibility format; selection policy belongs to the directory.
internal sealed class ClientPlayerSelectionStore : IClientPlayerSelectionStore
{
    private readonly ClashOfRimSettings settings;
    internal ClientPlayerSelectionStore(ClashOfRimSettings settings) => this.settings = settings;
    public ClientPlayerSelection Read() => new(settings.TargetUserId, settings.TargetColonyId, settings.TargetSnapshotId);
    public void Write(ClientPlayerSelection selection)
    { settings.TargetUserId = selection.UserId; settings.TargetColonyId = selection.ColonyId; settings.TargetSnapshotId = selection.SnapshotId ?? ""; }
    public void Persist() => settings.Write();
}
