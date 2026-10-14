using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed partial class MercenaryContractRegistry
{
    internal IReadOnlyList<MercenaryContractRecord> CapturePendingOperations(string user, string colony)
    {
        lock (gate) return contracts.Values.Where(contract => contract.UserId == user && contract.ColonyId == colony
            && contract.Status == StatusPendingActivation).ToArray();
    }

    internal void ValidatePendingOperations(PendingSnapshotOperations batch, LatestSnapshotRecord snapshot)
    {
        lock (gate)
        {
            foreach (var contract in batch.Mercenaries)
            {
                var confirmed = PendingSnapshotOperations.Confirm(contract, snapshot);
                if (!contracts.TryGetValue(contract.ContractId, out var current) || current != contract && current != confirmed
                    && current != confirmed with { Status = StatusCompleted })
                    throw new InvalidDataException("Pending mercenary changed after its snapshot commit was prepared.");
            }
        }
    }

    internal void ApplyPendingOperations(PendingSnapshotOperations batch, LatestSnapshotRecord snapshot)
    {
        lock (gate)
        {
            ValidatePendingOperations(batch, snapshot);
            var changed = batch.Mercenaries.Where(contract => contracts[contract.ContractId] == contract)
                .Select(contract => PendingSnapshotOperations.Confirm(contract, snapshot)).ToArray();
            if (changed.Length != 0) PersistContracts(changed, []);
        }
    }
}
