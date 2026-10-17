using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Network.Modules.Shop;

internal sealed class ShopSnapshotCommitter(SnapshotCommitService commits, IColonySnapshotIndexStore snapshots,
    BankLoanRegistry loans, ServerShopRegistry shop, MercenaryContractRegistry mercenaries, ISnapshotCommitEffects effects)
{
    internal void Commit(SaveSnapshotPackage package, SnapshotUploadResult upload, ShopSnapshotMutation mutation) =>
        commits.CommitShop(snapshots, loans, shop, package, upload.AcceptedSnapshot!, mutation, effects.AfterCommit,
            upload.SnapshotUploadKind, mercenaries, preparedStoragePayload: upload.PreparedStoragePayload);
}
