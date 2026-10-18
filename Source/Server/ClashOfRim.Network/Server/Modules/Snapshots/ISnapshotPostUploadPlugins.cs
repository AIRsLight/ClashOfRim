using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal interface ISnapshotPostUploadPlugins
{
    IEnumerable<SnapshotPostUploadOperation> Bind(SnapshotPostUploadInput input, IEnumerable<ISnapshotPostUploadProcessor> processors);
}
