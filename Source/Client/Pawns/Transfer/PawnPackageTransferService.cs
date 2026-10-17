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

            byte[]? previewPng = thing.PawnPackage.PendingPreviewPng;
            if (previewPng is { Length: > 0 })
            {
                await TryStorePawnPackagePreviewAsync(
                    client,
                    result.Response.PawnPackageId!,
                    previewPng).ConfigureAwait(false);
            }

            thing.PawnPackageId = result.Response.PawnPackageId;
            thing.PawnPackage = null;
        }
    }

    private static async Task TryStorePawnPackagePreviewAsync(
        ClashOfRimModNetworkClient client,
        string pawnPackageId,
        byte[] previewPng)
    {
        ClashOfRimClientNetworkResult<ModStorePawnPackagePreviewResponseDto> result =
            await client.StorePawnPackagePreviewAsync(
                pawnPackageId,
                previewPng).ConfigureAwait(false);
        if (!result.Success || result.Response is null)
        {
            Log.Warning("[ClashOfRim] Pawn preview upload failed for package "
                + pawnPackageId
                + ": "
                + (result.ErrorCode ?? "<unknown>")
                + " "
                + (result.Message ?? string.Empty));
            return;
        }

        if (result.Response.Result is { Accepted: false } response)
        {
            Log.Warning("[ClashOfRim] Pawn preview upload rejected for package "
                + pawnPackageId
                + ": "
                + response.ErrorCode
                + " "
                + (response.Message ?? string.Empty));
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
}
