using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;

namespace AIRsLight.ClashOfRim.Network.Modules.Transfers;

internal sealed class ItemTransferPackages(PawnPackageRegistry pawnsStore, ThingPackageRegistry thingsStore)
{
    internal bool TryStore(
        ProtocolIdentity owner,
        string idempotencyPrefix,
        IReadOnlyList<ThingReferenceDto> things,
        out IReadOnlyList<ThingReferenceDto> storedThings,
        out ProtocolResponse? failure)
    {
        storedThings = Array.Empty<ThingReferenceDto>();
        failure = null;
        if (!TryValidateInlinePawnPackages(things, out failure)
            || !TryValidateInlineThingPackages(things, out failure))
        {
            return false;
        }

        storedThings = things.Select((thing, index) =>
        {
            StoredThingPackageRecord? thingPackageRecord = null;
            if (thing.ThingPackage is not null)
            {
                if (!TryToThingStatePackage(thing.ThingPackage, out ThingStatePackage? thingPackage, out string thingPackageFailure)
                    || thingPackage is null)
                {
                    throw new InvalidOperationException(thingPackageFailure);
                }

                thingPackageRecord = thingsStore.Store(
                    idempotencyPrefix + ":thing:" + index + ":" + thing.GlobalKey,
                    owner.UserId,
                    owner.ColonyId,
                    owner.SnapshotId,
                    thingPackage,
                    DateTimeOffset.UtcNow);
            }

            if (thing.PawnPackage is null)
            {
                return thingPackageRecord is null
                    ? thing
                    : CloneThingReferenceWithStoredPackages(
                        thing,
                        pawnPackageId: thing.PawnPackageId,
                        thingPackageId: string.IsNullOrWhiteSpace(thing.ThingPackageId)
                            ? thingPackageRecord.PackageId
                            : thing.ThingPackageId,
                        metadata: CopyMetadata(thing.Metadata));
            }

            if (!TryToPawnExchangePackage(thing.PawnPackage, out PawnExchangePackage? package, out string packageFailure)
                || package is null)
            {
                throw new InvalidOperationException(packageFailure);
            }

            StoredPawnPackageRecord record = pawnsStore.Store(
                idempotencyPrefix + ":" + index + ":" + thing.GlobalKey,
                owner.UserId,
                owner.ColonyId,
                owner.SnapshotId,
                package,
                DateTimeOffset.UtcNow);

            return CloneThingReferenceWithStoredPackages(thing,
                pawnPackageId: string.IsNullOrWhiteSpace(thing.PawnPackageId) ? record.PackageId : thing.PawnPackageId,
                thingPackageId: thingPackageRecord is null || !string.IsNullOrWhiteSpace(thing.ThingPackageId)
                    ? thing.ThingPackageId : thingPackageRecord.PackageId,
                metadata: EnrichPawnSummaryMetadata(thing.Metadata, package));
        }).ToList();
        return true;
    }

}
