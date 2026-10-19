using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static async Task<IResult> ConfirmEventApplication(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<ConfirmEventApplicationMetadataRequest>? multipart =
            await ReadMultipartSnapshotRequest<ConfirmEventApplicationMetadataRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new ConfirmEventApplicationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.ConfirmMissingPayload")),
                eventId: string.Empty,
                appliedSnapshotId: null,
                serverValidationResult: "MissingMultipartPayload"));
        }

        ConfirmEventApplicationMetadataRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (AuthorizeColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                request.EventId,
                authorizationScope: null,
                nowUtc) is { } authFailure)
        {
            return Results.Ok(new ConfirmEventApplicationResponse(
                authFailure,
                request.EventId,
                appliedSnapshotId: null,
                serverValidationResult: "AuthFailed"));
        }

        bool playerRaidSettlement = state.RaidEvidenceConfirmation.Handles(request.EventId, request.SourceEventId);
        if (!playerRaidSettlement)
        {
            var response = ReceiveConfirmationSnapshot(state, request.UserId, request.ColonyId, request.AuthToken,
                request.ConfirmedSnapshot, multipart.Payload, new SnapshotConfirmationRequest(request.BaseSnapshotId,
                    [new ConfirmEventApplicationEntry(request.EventId, request.SourceEventId, request.ClientApplicationResult)], true), nowUtc);
            var result = response.Applications.FirstOrDefault();
            return Results.Ok(new ConfirmEventApplicationResponse(result?.Result ?? response.Result, request.EventId,
                result?.AppliedSnapshotId, result?.ServerValidationResult ?? "InvalidConfirmation", response.NextLineageToken));
        }
        return Results.Ok(state.RaidEvidenceConfirmation.Confirm(request, multipart.Payload,
            httpRequest.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault(), nowUtc));
    }

    private static async Task<IResult> ConfirmEventApplications(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<ConfirmEventApplicationsMetadataRequest>? multipart =
            await ReadMultipartSnapshotRequest<ConfirmEventApplicationsMetadataRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new ConfirmEventApplicationsResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.BatchConfirmMissingPayload")),
                appliedSnapshotId: null,
                applications: Array.Empty<ConfirmEventApplicationResultDto>()));
        }

        ConfirmEventApplicationsMetadataRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (AuthorizeColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc) is { } authFailure)
        {
            return Results.Ok(new ConfirmEventApplicationsResponse(
                authFailure,
                appliedSnapshotId: null,
                applications: Array.Empty<ConfirmEventApplicationResultDto>()));
        }

        return Results.Ok(ReceiveConfirmationSnapshot(state, request.UserId, request.ColonyId, request.AuthToken,
            request.ConfirmedSnapshot, multipart.Payload,
            new SnapshotConfirmationRequest(request.BaseSnapshotId, request.Applications, false), nowUtc));
    }
}
