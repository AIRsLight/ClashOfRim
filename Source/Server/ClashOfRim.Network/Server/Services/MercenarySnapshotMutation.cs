using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed record MercenarySnapshotMutation(MercenaryContractRecord? Contract = null,
    MercenaryGuardContractRecord? Guard = null, int MaximumOpenContracts = 0)
{
    internal void Validate(SnapshotIdentity identity)
    {
        if ((Contract is null) == (Guard is null)) throw new InvalidDataException("One mercenary mutation is required.");
        if (Contract is { } contract && (contract.UserId != identity.OwnerId || contract.ColonyId != identity.ColonyId
                || contract.SnapshotId != identity.SnapshotId || string.IsNullOrWhiteSpace(contract.ContractId)
                || string.IsNullOrWhiteSpace(contract.IdempotencyKey) || contract.Status != MercenaryContractRegistry.StatusActive
                || contract.ActivatedAtUtc != contract.CreatedAtUtc || MaximumOpenContracts <= 0)
            || Guard is { } guard && (guard.UserId != identity.OwnerId || guard.ColonyId != identity.ColonyId
                || guard.SnapshotId != identity.SnapshotId || string.IsNullOrWhiteSpace(guard.ContractId)
                || string.IsNullOrWhiteSpace(guard.IdempotencyKey) || guard.Status != MercenaryGuardContractRegistry.StatusActive
                || guard.ConsumedRaidEventId is not null || guard.ConsumedAtUtc is not null))
            throw new InvalidDataException("Mercenary journal does not match the confirmed snapshot.");
    }

    internal object Gate(MercenaryContractRegistry? contracts, MercenaryGuardContractRegistry? guards) =>
        Contract is not null ? (contracts ?? throw new InvalidOperationException("Mercenary recovery requires the contract registry.")).SnapshotCommitGate
            : (guards ?? throw new InvalidOperationException("Guard recovery requires the contract registry.")).SnapshotCommitGate;

    internal void Preflight(MercenaryContractRegistry? contracts, MercenaryGuardContractRegistry? guards)
    {
        if (Contract is { } contract) contracts!.ValidateSnapshotCommit(contract, MaximumOpenContracts);
        else guards!.ValidateSnapshotCommit(Guard!);
    }

    internal void Apply(MercenaryContractRegistry? contracts, MercenaryGuardContractRegistry? guards)
    {
        if (Contract is { } contract) contracts!.ApplySnapshotCommit(contract, MaximumOpenContracts);
        else guards!.ApplySnapshotCommit(Guard!);
    }
}
