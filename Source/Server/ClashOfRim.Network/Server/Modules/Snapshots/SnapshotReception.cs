using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

// Keep network admission failures alongside the upload outcome instead of
// reconstructing their category from the save layer's message/IdentityMismatch.
internal sealed class SnapshotReception
{
    internal SnapshotUploadResult Upload { get; }
    internal ProtocolResponse Result { get; }
    internal SnapshotReception(SnapshotUploadResult upload) : this(upload, SnapshotTransactions.ToProtocolResponse(upload)) { }
    private SnapshotReception(SnapshotUploadResult upload, ProtocolResponse result) { Upload = upload; Result = result; }
    internal static SnapshotReception Denied(ProtocolResponse rejection) => new(
        SnapshotUploadResult.Reject(SnapshotUploadResultKind.IdentityMismatch, rejection.Message ?? ServerLocalization.Text("Auth.Failed")), rejection);
}
