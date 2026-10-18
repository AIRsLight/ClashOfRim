using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult ListPlayers(ListPlayersRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
        {
            return Results.Ok(new ListPlayersResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Players.MissingIdentity")),
                Array.Empty<PlayerSummaryDto>()));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        state.PlayerActivity.Record(request.UserId, request.ColonyId, request.CurrentSnapshotId, nowUtc);
        LatestSnapshotLookup snapshotLookup = LatestSnapshotLookup.Build(state.SnapshotStore.ListLatest());
        List<PlayerSummaryDto> players = state.Players.List()
            .Select(record =>
            {
                string? currentSnapshotId = ResolveCurrentSnapshotId(snapshotLookup, record.UserId, record.ColonyId, record.CurrentSnapshotId);
                PlayerSnapshotWealthSummary snapshotSummary = IsSnapshotWealthCacheCurrent(record, currentSnapshotId)
                    ? new PlayerSnapshotWealthSummary(record.ColonyId, currentSnapshotId, record.LatestSnapshotWealth)
                    : state.PlayerWealth.CacheLatest(record.UserId, record.ColonyId, currentSnapshotId, nowUtc, snapshotLookup);
                return new PlayerSummaryDto(
                    record.UserId,
                    snapshotSummary.ColonyId,
                    snapshotSummary.CurrentSnapshotId,
                    state.OnlinePresence.IsUserOnline(record.UserId),
                    record.LastSeenAtUtc,
                    record.DisplayName,
                    ResolveDiplomacyRelationKind(
                        state,
                        request.UserId,
                        request.ColonyId,
                        record.UserId,
                        snapshotSummary.ColonyId),
                    snapshotSummary.LatestSnapshotWealth);
            })
            .Where(HasVisiblePlayerColony)
            .ToList();

        return Results.Ok(new ListPlayersResponse(
            ProtocolResponse.Ok(T("Players.Returned")),
            players));
    }

    private static IResult ListAchievements(ListAchievementsRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
        {
            return Results.Ok(new ListAchievementsResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Achievements.MissingIdentity")),
                Array.Empty<AchievementLeaderboardDto>(),
                Array.Empty<AchievementSummaryDto>()));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        state.PlayerActivity.Record(request.UserId, request.ColonyId, request.CurrentSnapshotId, nowUtc);
        string targetUserId = string.IsNullOrWhiteSpace(request.TargetUserId)
            ? request.UserId
            : request.TargetUserId!.Trim();
        string targetColonyId = string.IsNullOrWhiteSpace(request.TargetColonyId)
            ? request.ColonyId
            : request.TargetColonyId!.Trim();
        IReadOnlyList<PlayerSessionRecord> playerRecords = state.Players.List();
        LatestSnapshotLookup snapshotLookup = LatestSnapshotLookup.Build(state.SnapshotStore.ListLatest());
        HashSet<string> visiblePlayerColonies = BuildVisiblePlayerColonyKeys(playerRecords, snapshotLookup);
        Dictionary<string, PlayerSessionRecord> playersByUser = playerRecords
            .GroupBy(player => player.UserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        IReadOnlyList<AchievementAggregateRecord> aggregates = state.Achievements.ListAggregates()
            .Where(record => visiblePlayerColonies.Contains(PlayerColonyKey(record.UserId, record.ColonyId)))
            .ToArray();
        IReadOnlyList<AchievementSummaryDto> ownAchievements = aggregates
            .Where(record => string.Equals(record.UserId, targetUserId, StringComparison.Ordinal)
                && string.Equals(record.ColonyId, targetColonyId, StringComparison.Ordinal))
            .OrderBy(record => record.Category, StringComparer.Ordinal)
            .ThenBy(record => record.AchievementId, StringComparer.Ordinal)
            .Select(record => new AchievementSummaryDto(
                record.AchievementId,
                record.Category,
                record.LabelKey,
                AchievementDescriptionKey(record.LabelKey, record.AchievementId),
                record.IconId,
                record.Color,
                record.Value,
                record.SourceSnapshotId))
            .ToArray();

        IReadOnlyList<AchievementLeaderboardDto> leaderboards = aggregates
            .GroupBy(record => record.AchievementId, StringComparer.Ordinal)
            .Select(group =>
            {
                AchievementAggregateRecord first = group.First();
                IReadOnlyList<AchievementLeaderboardEntryDto> entries = group
                    .OrderByDescending(record => record.Value)
                    .ThenBy(record => record.UserId, StringComparer.Ordinal)
                    .ThenBy(record => record.ColonyId, StringComparer.Ordinal)
                    .Take(10)
                    .Select(record =>
                    {
                        playersByUser.TryGetValue(record.UserId, out PlayerSessionRecord? player);
                        return new AchievementLeaderboardEntryDto(
                            record.UserId,
                            record.ColonyId,
                            player?.DisplayName,
                            record.Value,
                            record.SourceSnapshotId);
                    })
                    .ToArray();

                return new AchievementLeaderboardDto(
                    first.AchievementId,
                    first.Category,
                    first.LabelKey,
                    AchievementDescriptionKey(first.LabelKey, first.AchievementId),
                    first.IconId,
                    first.Color,
                    entries);
            })
            .OrderBy(board => board.Category, StringComparer.Ordinal)
            .ThenBy(board => board.AchievementId, StringComparer.Ordinal)
            .ToArray();

        return Results.Ok(new ListAchievementsResponse(
            ProtocolResponse.Ok(T("Achievements.Returned")),
            leaderboards,
            ownAchievements));
    }

    private static string AchievementDescriptionKey(string? labelKey, string? achievementId)
    {
        if (!string.IsNullOrWhiteSpace(labelKey))
        {
            return labelKey!.Trim() + ".Description";
        }

        return string.IsNullOrWhiteSpace(achievementId)
            ? "ClashOfRim.Achievement.Unknown.Description"
            : "ClashOfRim.Achievement." + achievementId!.Trim() + ".Description";
    }

    private static bool IsSnapshotWealthCacheCurrent(PlayerSessionRecord record, string? currentSnapshotId)
    {
        return !string.IsNullOrWhiteSpace(currentSnapshotId)
            && string.Equals(record.LatestSnapshotWealthSnapshotId, currentSnapshotId, StringComparison.Ordinal);
    }

    private static bool HasVisiblePlayerColony(PlayerSummaryDto player)
    {
        return !string.IsNullOrWhiteSpace(player.ColonyId)
            && !string.IsNullOrWhiteSpace(player.CurrentSnapshotId);
    }

    private static HashSet<string> BuildVisiblePlayerColonyKeys(
        IReadOnlyList<PlayerSessionRecord> players,
        LatestSnapshotLookup snapshotLookup)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        foreach (PlayerSessionRecord player in players)
        {
            string? currentSnapshotId = ResolveCurrentSnapshotId(
                snapshotLookup,
                player.UserId,
                player.ColonyId,
                player.CurrentSnapshotId);
            if (string.IsNullOrWhiteSpace(currentSnapshotId)
                || string.IsNullOrWhiteSpace(player.UserId)
                || string.IsNullOrWhiteSpace(player.ColonyId))
            {
                continue;
            }

            LatestSnapshotRecord? snapshot = snapshotLookup.Resolve(player.UserId, player.ColonyId);
            string colonyId = !string.IsNullOrWhiteSpace(snapshot?.Identity.ColonyId)
                ? snapshot!.Identity.ColonyId!
                : player.ColonyId;
            visible.Add(PlayerColonyKey(player.UserId, colonyId));
        }

        return visible;
    }

    private static string PlayerColonyKey(string userId, string colonyId)
    {
        return (userId ?? string.Empty) + "\u001f" + (colonyId ?? string.Empty);
    }

    private static string? ResolveCurrentSnapshotId(
        LatestSnapshotLookup snapshotLookup,
        string userId,
        string colonyId,
        string? currentSnapshotId)
    {
        LatestSnapshotRecord? snapshot = snapshotLookup.Resolve(userId, colonyId);
        if (snapshot is not null)
        {
            return snapshot.Identity.SnapshotId;
        }

        return !string.IsNullOrWhiteSpace(currentSnapshotId)
            ? currentSnapshotId
            : null;
    }

    private static string? FindSnapshotNextLineageToken(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string? snapshotId)
        => state.SnapshotTransactions.FindSnapshotNextLineageToken(userId, colonyId, snapshotId);

    private static IResult SendChatMessage(SendChatMessageRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
        {
            return Results.Ok(new SendChatMessageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Chat.SendMissingIdentity")),
                message: null));
        }

        if (!IsAuthorizedForColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            return Results.Ok(new SendChatMessageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, authFailure),
                message: null));
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.Ok(new SendChatMessageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Chat.EmptyContent")),
                message: null));
        }

        string? targetUserId = string.IsNullOrWhiteSpace(request.TargetUserId)
            ? null
            : request.TargetUserId!.Trim();
        if (!string.IsNullOrWhiteSpace(targetUserId)
            && !state.Players.ContainsUser(targetUserId))
        {
            return Results.Ok(new SendChatMessageResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Chat.PrivateTargetNotFound")),
                message: null));
        }

        ChatMessageRecord record = state.ChatMessages.Add(
            request.UserId,
            request.ColonyId,
            targetUserId,
            request.Text,
            nowUtc);

        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            state.ChatNotifications.SignalUsers(state.Players.List().Select(player => player.UserId));
        }
        else
        {
            state.ChatNotifications.SignalUsers(new[] { request.UserId, targetUserId! });
        }

        return Results.Ok(new SendChatMessageResponse(
            ProtocolResponse.Ok(T("Chat.Sent")),
            ToChatMessageDto(record)));
    }

    private static IResult ListChatMessages(ListChatMessagesRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
        {
            return Results.Ok(new ListChatMessagesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Chat.ListMissingIdentity")),
                Array.Empty<ChatMessageDto>(),
                latestSequence: Math.Max(0, request.AfterSequence)));
        }

        if (!IsAuthorizedForColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            return Results.Ok(new ListChatMessagesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, authFailure),
                Array.Empty<ChatMessageDto>(),
                latestSequence: Math.Max(0, request.AfterSequence)));
        }

        IReadOnlyList<ChatMessageDto> messages = state.ChatMessages
            .ListVisible(request.UserId, request.AfterSequence, request.Limit)
            .Select(ToChatMessageDto)
            .ToList();
        long latest = messages.Count == 0
            ? Math.Max(0, request.AfterSequence)
            : messages.Max(message => message.Sequence);

        return Results.Ok(new ListChatMessagesResponse(
            ProtocolResponse.Ok(T("Chat.Returned")),
            messages,
            latest));
    }

    private static async Task<IResult> UploadSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<UploadSnapshotMetadataRequest>? multipart = await ReadMultipartSnapshotRequest<UploadSnapshotMetadataRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new UploadSnapshotResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Snapshot.UploadMissingPayload")),
                acceptedSnapshotId: null,
                uploadResult: "MissingMultipartPayload"));
        }

        UploadSnapshotMetadataRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        string? Authorize() => IsAuthorizedForColony(state, request.AuthToken, request.UserId, request.ColonyId,
            null, null, DateTimeOffset.UtcNow, out string failure) ? null : failure;
        using (var recovery = EnterColonySnapshotMutation(state, request.AuthToken, request.UserId, request.ColonyId))
        {
            if (recovery.Rejection is { } failure)
                return Results.Ok(new UploadSnapshotResponse(failure, acceptedSnapshotId: null, uploadResult: "AuthFailed"));
        }
        // Timeout processing is server maintenance. Admit the caller before starting it,
        // then revalidate the caller after preparing its own snapshot and recovering new work.
        state.RaidTimeouts.Reconcile(nowUtc);

        if (PendingConfirmationReconciler.Rejection(state.PendingConfirmations.ReconcileAfterRecovery(
                request.UserId, request.ColonyId, nowUtc, forceCancel: false)) is { } pendingConfirmationFailure)
        {
            return Results.Ok(new UploadSnapshotResponse(
                pendingConfirmationFailure,
                acceptedSnapshotId: null,
                uploadResult: "PendingConfirmationExpired"));
        }

        SnapshotUploadResult upload = ReceiveSnapshotCore(
            state,
            request.UserId,
            request.ColonyId,
            request.SnapshotId,
            request.Package,
            multipart.Payload,
            nowUtc,
            confirmationOperation: request.ConfirmationOperation,
            postUploadData: new SnapshotPostUploadExtraData(request.Package.SnapshotUploadKind, request.ConfirmationOperation, request.AchievementCandidates),
            authorizeCommit: Authorize);
        if (!upload.Accepted)
        {
            state.RuntimeLogger.LogWarning(
                "Snapshot upload rejected: user={UserId} colony={ColonyId} snapshot={SnapshotId} previous={PreviousSnapshotId} kind={Kind} message={Message} operation={Operation}",
                request.UserId,
                request.ColonyId,
                request.SnapshotId,
                request.Package.PreviousSnapshotId,
                upload.Kind,
                upload.Message,
                request.ConfirmationOperation);
        }
        return Results.Ok(new UploadSnapshotResponse(
            ToProtocolResponse(upload),
            upload.AcceptedSnapshot?.Identity.SnapshotId,
            upload.Kind.ToString(),
            upload.AcceptedSnapshot?.Envelope.NextLineageToken));
    }

    private static IResult DownloadLatestSnapshot(DownloadLatestSnapshotRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return Results.Ok(new DownloadLatestSnapshotResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Snapshot.DownloadMissingIdentity")), null, null));

        var download = CaptureSnapshotDownload(state, request.UserId, request.ColonyId, request.AuthToken,
            request.AuthorizationEventId, request.AuthorizationScope, requestedSnapshotId: null);
        return Results.Ok(new DownloadLatestSnapshotResponse(download.Result, download.Package?.Envelope.Identity.SnapshotId,
            download.Package is null ? null : ProtocolDtoMapper.ToMetadataDto(download.Package), download.Recovery));
    }

    private static IResult DownloadLatestSnapshotPayload(DownloadLatestSnapshotPayloadRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.SnapshotId))
            return Results.BadRequest(new { result = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                T("Snapshot.PayloadDownloadMissingIdentity")) });

        var download = CaptureSnapshotDownload(state, request.UserId, request.ColonyId, request.AuthToken,
            request.AuthorizationEventId, request.AuthorizationScope, request.SnapshotId);
        if (download.Package is null) return Results.Json(new { result = download.Result }, statusCode: download.HttpStatusCode);
        return Results.File(download.Package.Payload, "application/octet-stream",
            (download.Package.Envelope.Identity.SnapshotId ?? "latest") + ".payload");
    }

    private sealed record SnapshotDownloadResult(ProtocolResponse Result, SaveSnapshotPackage? Package,
        ActiveRaidRecoveryDto? Recovery = null, int HttpStatusCode = StatusCodes.Status200OK);

    private static SnapshotDownloadResult CaptureSnapshotDownload(ClashOfRimNetworkState state, string userId, string colonyId,
        string? authToken, string? authorizationEventId, string? authorizationScope, string? requestedSnapshotId)
    {
        ProtocolResponse? Authorize() => IsAuthorizedForColony(state, authToken, userId, colonyId,
            authorizationEventId, authorizationScope, DateTimeOffset.UtcNow, out string failure)
                ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure);
        SnapshotDownloadResult Denied(ProtocolResponse failure) => new(failure, null, HttpStatusCode: StatusCodes.Status401Unauthorized);

        using (var recovery = EnterRecoveredSnapshotMutation(state, Authorize))
        {
            if (recovery.Rejection is { } failure) return Denied(failure);
        }
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        // Timeout maintenance is server work; the caller's access is checked again before cleanup and delivery.
        if (requestedSnapshotId is null) state.RaidTimeouts.Reconcile(nowUtc);
        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Snapshot.StoreDownloadUnsupported")),
                null, HttpStatusCode: StatusCodes.Status500InternalServerError);

        var package = packageStore.GetLatestPackage(userId, colonyId);
        if (package is not null)
            package = state.RaidBattleCleanup.BeforeDownload(package, userId, colonyId, nowUtc, Authorize);

        using (state.EnterSnapshotMutation())
        {
            if (Authorize() is { } failure) return Denied(failure);
            if (package is null)
                return new(ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound,
                    T(requestedSnapshotId is null ? "Snapshot.LatestNotFound" : "Snapshot.PayloadNotFound")),
                    null, HttpStatusCode: StatusCodes.Status404NotFound);
            // A skipped cleanup returns its captured package. It must still be current at the response boundary.
            if (!SnapshotCommitService.SameVersion(packageStore.GetLatest(userId, colonyId)?.Envelope, package.Envelope))
                return new(ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Snapshot.PayloadNotFound")),
                    null, HttpStatusCode: StatusCodes.Status404NotFound);
            if (requestedSnapshotId is not null && package.Envelope.Identity.SnapshotId != requestedSnapshotId)
                return new(ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Snapshot.PayloadNotFound")),
                    null, HttpStatusCode: StatusCodes.Status404NotFound);
            return new(ProtocolResponse.Ok(T("Snapshot.LatestReturned")), package,
                requestedSnapshotId is null ? state.RaidQueries.Recovery(userId, colonyId, DateTimeOffset.UtcNow) : null);
        }
    }

    private static ChatMessageDto ToChatMessageDto(ChatMessageRecord message)
    {
        return new ChatMessageDto(
            message.Sequence,
            message.MessageId,
            message.Channel,
            message.FromUserId,
            message.FromColonyId,
            message.TargetUserId,
            message.Text,
            message.SentAtUtc);
    }
}
