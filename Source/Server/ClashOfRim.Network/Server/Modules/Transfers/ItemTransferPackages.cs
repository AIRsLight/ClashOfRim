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

    internal StorePawnPackageResponse StorePawn(StorePawnPackageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || request.PawnPackage is null)
        {
            return new StorePawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("PawnPackage.UploadMissingFields")),
                pawnPackageId: null,
                pawnGlobalId: null);
        }

        if (!TryToPawnExchangePackage(request.PawnPackage, out PawnExchangePackage? package, out string packageFailure)
            || package is null)
        {
            return new StorePawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("PawnPackage.Invalid", ("MESSAGE", packageFailure))),
                pawnPackageId: null,
                pawnGlobalId: null);
        }

        try
        {
            StoredPawnPackageRecord record = pawnsStore.Store(
                request.IdempotencyKey,
                request.Owner.UserId,
                request.Owner.ColonyId,
                request.Owner.SnapshotId,
                package,
                DateTimeOffset.UtcNow);
            return new StorePawnPackageResponse(
                ProtocolResponse.Ok(ServerText.Format("PawnPackage.Stored")),
                record.PackageId,
                record.PawnGlobalId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new StorePawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("PawnPackage.Invalid", ("MESSAGE", ex.Message))),
                pawnPackageId: null,
                pawnGlobalId: null);
        }
    }

    internal GetPawnPackageResponse ReadPawn(GetPawnPackageRequest request)
    {
        if (request.Requester is null
            || string.IsNullOrWhiteSpace(request.Requester.UserId)
            || string.IsNullOrWhiteSpace(request.PawnPackageId))
        {
            return new GetPawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("PawnPackage.DownloadMissingFields")),
                request.PawnPackageId,
                pawnPackage: null);
        }

        if (!pawnsStore.TryGetPackage(request.PawnPackageId, out PawnExchangePackage? package, out string message)
            || package is null)
        {
            return new GetPawnPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, message),
                request.PawnPackageId,
                pawnPackage: null);
        }

        return new GetPawnPackageResponse(
            ProtocolResponse.Ok(ServerText.Format("PawnPackage.Returned")),
            request.PawnPackageId,
            ToPawnExchangePackageDto(package));
    }

    internal StoreThingPackageResponse StoreThing(StoreThingPackageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || request.ThingPackage is null)
        {
            return new StoreThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "thing package upload missing fields"),
                thingPackageId: null,
                fingerprint: null);
        }

        if (!TryToThingStatePackage(request.ThingPackage, out ThingStatePackage? package, out string packageFailure)
            || package is null)
        {
            return new StoreThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "invalid thing package: " + packageFailure),
                thingPackageId: null,
                fingerprint: null);
        }

        try
        {
            StoredThingPackageRecord record = thingsStore.Store(
                request.IdempotencyKey,
                request.Owner.UserId,
                request.Owner.ColonyId,
                request.Owner.SnapshotId,
                package,
                DateTimeOffset.UtcNow);
            return new StoreThingPackageResponse(
                ProtocolResponse.Ok("thing package stored"),
                record.PackageId,
                record.Fingerprint);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or IOException)
        {
            return new StoreThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "invalid thing package: " + ex.Message),
                thingPackageId: null,
                fingerprint: null);
        }
    }

    internal GetThingPackageResponse ReadThing(GetThingPackageRequest request)
    {
        if (request.Requester is null
            || string.IsNullOrWhiteSpace(request.Requester.UserId)
            || string.IsNullOrWhiteSpace(request.ThingPackageId))
        {
            return new GetThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "thing package download missing fields"),
                request.ThingPackageId,
                thingPackage: null);
        }

        if (!thingsStore.TryGetPackage(request.ThingPackageId, out ThingStatePackage? package, out string message)
            || package is null)
        {
            return new GetThingPackageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, message),
                request.ThingPackageId,
                thingPackage: null);
        }

        return new GetThingPackageResponse(
            ProtocolResponse.Ok("thing package returned"),
            request.ThingPackageId,
            ToThingStatePackageDto(package));
    }
}
