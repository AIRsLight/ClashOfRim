using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnPackageHydration
{
    public static async Task HydrateThingPawnPackagesAsync(
        IClientPackageReader client,
        IReadOnlyList<ModThingReferenceDto> things,
        CancellationToken cancellationToken = default)
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

            thing.PawnPackage = await DownloadPawnPackageOrThrowAsync(client, thing.PawnPackageId!, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task HydrateThingStatePackagesAsync(
        IClientPackageReader client,
        IReadOnlyList<ModThingReferenceDto> things,
        CancellationToken cancellationToken = default)
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

            thing.ThingPackage = await DownloadThingPackageOrThrowAsync(client, thing.ThingPackageId!, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<PawnPackageTransferResult> HydrateGiftItemsAsync(
        IClientPackageReader client,
        IReadOnlyList<GiftItemReference> items,
        CancellationToken cancellationToken = default)
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
                item.PawnPackage = await DownloadPawnPackageOrThrowAsync(client, item.PawnPackageId!, cancellationToken).ConfigureAwait(false);
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
        IClientPackageReader client,
        IReadOnlyList<GiftItemReference> items,
        CancellationToken cancellationToken = default)
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
                item.ThingPackage = await DownloadThingPackageOrThrowAsync(client, item.ThingPackageId!, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return PawnPackageTransferResult.Failed(ex.Message);
            }
        }

        return PawnPackageTransferResult.Ok();
    }

    private static async Task<ModPawnExchangePackageDto> DownloadPawnPackageOrThrowAsync(
        IClientPackageReader client,
        string pawnPackageId, CancellationToken cancellationToken)
    {
        ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto> result =
            await client.GetPawnPackageAsync(pawnPackageId, cancellationToken).ConfigureAwait(false);
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
        IClientPackageReader client,
        string thingPackageId, CancellationToken cancellationToken)
    {
        ClashOfRimClientNetworkResult<ModGetThingPackageResponseDto> result =
            await client.GetThingPackageAsync(thingPackageId, cancellationToken).ConfigureAwait(false);
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
