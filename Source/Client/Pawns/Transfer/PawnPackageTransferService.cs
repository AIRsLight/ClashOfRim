using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnPackageTransferService
{
    public static async Task StoreThingPawnPackagesAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<ModThingReferenceDto> things,
        string idempotencyPrefix)
    {
        if (client is null || things is null)
        {
            return;
        }

        for (int index = 0; index < things.Count; index++)
        {
            ModThingReferenceDto thing = things[index];
            if (thing.PawnPackage is null || !string.IsNullOrWhiteSpace(thing.PawnPackageId))
            {
                continue;
            }

            ClashOfRimClientNetworkResult<ModStorePawnPackageResponseDto> result =
                await client.StorePawnPackageAsync(
                    $"{idempotencyPrefix}:pawn-package:{index}:{thing.GlobalKey}",
                    thing.PawnPackage).ConfigureAwait(false);
            if (!result.Success || result.Response is null)
            {
                throw new InvalidOperationException(ClashOfRimText.Key(
                    "ClashOfRim.PawnPackage.UploadFailed",
                    (result.ErrorCode?.ToString() ?? string.Empty).Named("CODE"),
                    (result.Message ?? string.Empty).Named("MESSAGE")));
            }

            ModProtocolResponseDto? response = result.Response.Result;
            if (response is not null && !response.Accepted)
            {
                throw new InvalidOperationException(ClashOfRimText.Key(
                    "ClashOfRim.PawnPackage.UploadRejected",
                    response.ErrorCode.ToString().Named("CODE"),
                    (response.Message ?? string.Empty).Named("MESSAGE")));
            }

            if (string.IsNullOrWhiteSpace(result.Response.PawnPackageId))
            {
                throw new InvalidOperationException(ClashOfRimText.Key("ClashOfRim.PawnPackage.StoreMissingId"));
            }

            thing.PawnPackageId = result.Response.PawnPackageId;
            thing.PawnPackage = null;
        }
    }

    public static async Task StoreThingStatePackagesAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<ModThingReferenceDto> things,
        string idempotencyPrefix)
    {
        if (client is null || things is null)
        {
            return;
        }

        for (int index = 0; index < things.Count; index++)
        {
            ModThingReferenceDto thing = things[index];
            if (thing.ThingPackage is null || !string.IsNullOrWhiteSpace(thing.ThingPackageId))
            {
                continue;
            }

            ClashOfRimClientNetworkResult<ModStoreThingPackageResponseDto> result =
                await client.StoreThingPackageAsync(
                    $"{idempotencyPrefix}:thing-package:{index}:{thing.GlobalKey}",
                    thing.ThingPackage).ConfigureAwait(false);
            if (!result.Success || result.Response is null)
            {
                throw new InvalidOperationException(ClashOfRimText.Key(
                    "ClashOfRim.PawnPackage.UploadFailed",
                    (result.ErrorCode?.ToString() ?? string.Empty).Named("CODE"),
                    (result.Message ?? string.Empty).Named("MESSAGE")));
            }

            ModProtocolResponseDto? response = result.Response.Result;
            if (response is not null && !response.Accepted)
            {
                throw new InvalidOperationException(ClashOfRimText.Key(
                    "ClashOfRim.PawnPackage.UploadRejected",
                    response.ErrorCode.ToString().Named("CODE"),
                    (response.Message ?? string.Empty).Named("MESSAGE")));
            }

            if (string.IsNullOrWhiteSpace(result.Response.ThingPackageId))
            {
                throw new InvalidOperationException(ClashOfRimText.Key("ClashOfRim.PawnPackage.StoreMissingId"));
            }

            thing.ThingPackageId = result.Response.ThingPackageId;
            thing.ThingPackage = null;
        }
    }

    public static async Task HydrateThingPawnPackagesAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<ModThingReferenceDto> things)
    {
        if (client is null || things is null)
        {
            return;
        }

        foreach (ModThingReferenceDto thing in things)
        {
            if (thing.PawnPackage is not null || string.IsNullOrWhiteSpace(thing.PawnPackageId))
            {
                continue;
            }

            thing.PawnPackage = await DownloadPawnPackageOrThrowAsync(client, thing.PawnPackageId!).ConfigureAwait(false);
        }
    }

    public static async Task HydrateThingStatePackagesAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<ModThingReferenceDto> things)
    {
        if (client is null || things is null)
        {
            return;
        }

        foreach (ModThingReferenceDto thing in things)
        {
            if (thing.ThingPackage is not null || string.IsNullOrWhiteSpace(thing.ThingPackageId))
            {
                continue;
            }

            thing.ThingPackage = await DownloadThingPackageOrThrowAsync(client, thing.ThingPackageId!).ConfigureAwait(false);
        }
    }

    public static async Task<PawnPackageTransferResult> HydrateGiftItemsAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<GiftItemReference> items)
    {
        if (client is null || items is null)
        {
            return PawnPackageTransferResult.Ok();
        }

        foreach (GiftItemReference item in items)
        {
            if (item.PawnPackage is not null || string.IsNullOrWhiteSpace(item.PawnPackageId))
            {
                continue;
            }

            try
            {
                ClashLog.Message(
                    "[ClashOfRim][PawnPackage] downloading gift pawn package id="
                    + item.PawnPackageId
                    + " key="
                    + item.GlobalKey
                    + " def="
                    + (item.DefName ?? "<null>")
                    + ".");
                item.PawnPackage = await DownloadPawnPackageOrThrowAsync(client, item.PawnPackageId!).ConfigureAwait(false);
                ClashLog.Message(
                    "[ClashOfRim][PawnPackage] downloaded gift pawn package id="
                    + item.PawnPackageId
                    + " global="
                    + (item.PawnPackage.Reference?.GlobalId ?? "<null>")
                    + " thingDef="
                    + (item.PawnPackage.Identity?.ThingDef ?? "<null>")
                    + " extensions="
                    + item.PawnPackage.Extensions.Count
                    + " scribeBytes="
                    + (item.PawnPackage.Scribe?.Xml?.Length ?? 0)
                    + ".");
            }
            catch (InvalidOperationException ex)
            {
                return PawnPackageTransferResult.Failed(ex.Message);
            }
        }

        return PawnPackageTransferResult.Ok();
    }

    public static async Task<PawnPackageTransferResult> HydrateGiftThingStatePackagesAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<GiftItemReference> items)
    {
        if (client is null || items is null)
        {
            return PawnPackageTransferResult.Ok();
        }

        foreach (GiftItemReference item in items)
        {
            if (item.ThingPackage is not null || string.IsNullOrWhiteSpace(item.ThingPackageId))
            {
                continue;
            }

            try
            {
                item.ThingPackage = await DownloadThingPackageOrThrowAsync(client, item.ThingPackageId!).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return PawnPackageTransferResult.Failed(ex.Message);
            }
        }

        return PawnPackageTransferResult.Ok();
    }

    private static async Task<ModPawnExchangePackageDto> DownloadPawnPackageOrThrowAsync(
        ClashOfRimModNetworkClient client,
        string pawnPackageId)
    {
        ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto> result =
            await client.GetPawnPackageAsync(pawnPackageId).ConfigureAwait(false);
        if (!result.Success || result.Response is null)
        {
            throw new InvalidOperationException(ClashOfRimText.Key(
                "ClashOfRim.PawnPackage.DownloadFailed",
                (result.ErrorCode?.ToString() ?? string.Empty).Named("CODE"),
                (result.Message ?? string.Empty).Named("MESSAGE")));
        }

        ModProtocolResponseDto? response = result.Response.Result;
        if (response is not null && !response.Accepted)
        {
            throw new InvalidOperationException(ClashOfRimText.Key(
                "ClashOfRim.PawnPackage.DownloadRejected",
                response.ErrorCode.ToString().Named("CODE"),
                (response.Message ?? string.Empty).Named("MESSAGE")));
        }

        if (result.Response.PawnPackage is null)
        {
            throw new InvalidOperationException(ClashOfRimText.Key("ClashOfRim.PawnPackage.DownloadMissingPackage"));
        }

        return result.Response.PawnPackage;
    }

    private static async Task<ModThingStatePackageDto> DownloadThingPackageOrThrowAsync(
        ClashOfRimModNetworkClient client,
        string thingPackageId)
    {
        ClashOfRimClientNetworkResult<ModGetThingPackageResponseDto> result =
            await client.GetThingPackageAsync(thingPackageId).ConfigureAwait(false);
        if (!result.Success || result.Response is null)
        {
            throw new InvalidOperationException(ClashOfRimText.Key(
                "ClashOfRim.PawnPackage.DownloadFailed",
                (result.ErrorCode?.ToString() ?? string.Empty).Named("CODE"),
                (result.Message ?? string.Empty).Named("MESSAGE")));
        }

        ModProtocolResponseDto? response = result.Response.Result;
        if (response is not null && !response.Accepted)
        {
            throw new InvalidOperationException(ClashOfRimText.Key(
                "ClashOfRim.PawnPackage.DownloadRejected",
                response.ErrorCode.ToString().Named("CODE"),
                (response.Message ?? string.Empty).Named("MESSAGE")));
        }

        if (result.Response.ThingPackage is null)
        {
            throw new InvalidOperationException(ClashOfRimText.Key("ClashOfRim.PawnPackage.DownloadMissingPackage"));
        }

        return result.Response.ThingPackage;
    }
}
