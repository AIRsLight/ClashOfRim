using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static async Task StreamSession(
        HttpContext context,
        ClashOfRimNetworkState state,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory)
    {
        string userId = context.Request.Query["userId"].ToString();
        string colonyId = context.Request.Query["colonyId"].ToString();
        string currentSnapshotId = context.Request.Query["currentSnapshotId"].ToString();
        string sessionId = context.Request.Query["sessionId"].ToString();
        string knownVersionText = context.Request.Query["knownNotificationVersion"].ToString();
        long knownVersion = long.TryParse(knownVersionText, out long parsedVersion) ? Math.Max(0, parsedVersion) : 0;
        string knownWorldConfigurationVersionText = context.Request.Query["knownWorldConfigurationVersion"].ToString();
        long knownWorldConfigurationVersion = long.TryParse(knownWorldConfigurationVersionText, out long parsedWorldConfigurationVersion)
            ? Math.Max(0, parsedWorldConfigurationVersion)
            : 0;
        using CancellationTokenSource streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            lifetime.ApplicationStopping);
        CancellationToken cancellationToken = streamCancellation.Token;

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId))
        {
            await RejectSessionStreamRequestAsync(
                context,
                StatusCodes.Status400BadRequest,
                ProtocolErrorCode.ValidationFailed,
                T("SessionStream.MissingIdentity"),
                cancellationToken);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await RejectSessionStreamRequestAsync(
                context,
                StatusCodes.Status400BadRequest,
                ProtocolErrorCode.ValidationFailed,
                "Session stream requires WebSocket.",
                cancellationToken);
            return;
        }

        if (!state.LoginSessions.TryBeginStream(userId, colonyId, sessionId, DateTimeOffset.UtcNow))
        {
            await RejectSessionStreamRequestAsync(
                context,
                StatusCodes.Status409Conflict,
                ProtocolErrorCode.ServerRejected,
                T("SessionStream.InvalidSession"),
                cancellationToken);
            return;
        }

        if (!state.OnlinePresence.TryConnectExclusive(userId, out OnlinePresenceLease? presence))
        {
            state.LoginSessions.End(userId, sessionId);
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            RuntimeLogger(loggerFactory).LogWarning(
                "玩家上线被拒绝：user={UserId} colony={ColonyId} session={SessionId} reason=AlreadyOnline",
                userId,
                colonyId,
                sessionId);
            await RejectSessionStreamRequestAsync(
                context,
                StatusCodes.Status409Conflict,
                ProtocolErrorCode.ServerRejected,
                T("SessionStream.AlreadyOnline"),
                cancellationToken);
            return;
        }

        WebSocket? webSocket = null;
        Task? receiveTask = null;
        using var sendGate = new SemaphoreSlim(1, 1);
        try
        {
            webSocket = await context.WebSockets.AcceptWebSocketAsync();
            receiveTask = ReceiveSessionWebSocketMessagesAsync(
                webSocket,
                presence!,
                state,
                userId,
                colonyId,
                sessionId,
                sendGate,
                streamCancellation,
                cancellationToken);
            RecordPlayerSeen(state, userId, colonyId, currentSnapshotId, DateTimeOffset.UtcNow);
            RuntimeLogger(loggerFactory).LogInformation(
                "玩家上线：user={UserId} colony={ColonyId} snapshot={SnapshotId} session={SessionId} channel=ws",
                userId,
                colonyId,
                currentSnapshotId,
                sessionId);
            long notificationVersion = state.EventNotifications.GetVersion(userId);
            if (notificationVersion > knownVersion)
            {
                knownVersion = notificationVersion;
            }

            long knownChatVersion = state.ChatNotifications.GetVersion(userId);
            long knownPlayerOnlineVersion = state.PlayerOnlineNotifications.GetVersion(userId);

            await SendSessionWebSocketEventAsync(
                webSocket,
                "presence",
                new SessionStreamEventDto(userId, online: true, knownVersion, T("SessionStream.Connected")),
                sendGate,
                cancellationToken);
            state.PlayerOnlineNotifications.SignalPlayerOnline(
                userId,
                state.OnlinePresence.ListOnlineUsers(),
                DateTimeOffset.UtcNow);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (webSocket.State is WebSocketState.Closed or WebSocketState.Aborted)
                {
                    break;
                }

                if (!presence!.IsActive())
                {
                    await SendSessionWebSocketEventAsync(
                        webSocket,
                        "error",
                        new SessionStreamEventDto(userId, online: false, knownVersion, T("SessionStream.LeaseExpired")),
                        sendGate,
                        cancellationToken);
                    break;
                }

                if (!state.LoginSessions.IsValid(userId, colonyId, sessionId, DateTimeOffset.UtcNow))
                {
                    await SendSessionWebSocketEventAsync(
                        webSocket,
                        "error",
                        new SessionStreamEventDto(userId, online: false, knownVersion, T("Auth.SessionExpired")),
                        sendGate,
                        cancellationToken);
                    break;
                }

                EventNotificationWaitResult wait;
                EventNotificationWaitResult chatWait;
                EventNotificationWaitResult worldConfigurationWait;
                PlayerOnlineNotificationWaitResult playerOnlineWait;
                try
                {
                    using CancellationTokenSource waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task<EventNotificationWaitResult> ledgerWaitTask = state.EventNotifications.WaitAsync(
                        userId,
                        knownVersion,
                        SessionStreamWaitTimeout,
                        waitCancellation.Token);
                    Task<EventNotificationWaitResult> chatWaitTask = state.ChatNotifications.WaitAsync(
                        userId,
                        knownChatVersion,
                        SessionStreamWaitTimeout,
                        waitCancellation.Token);
                    Task<EventNotificationWaitResult> worldConfigurationWaitTask = state.WorldConfigurationNotifications.WaitAsync(
                        userId,
                        knownWorldConfigurationVersion,
                        SessionStreamWaitTimeout,
                        waitCancellation.Token);
                    Task<PlayerOnlineNotificationWaitResult> playerOnlineWaitTask = state.PlayerOnlineNotifications.WaitAsync(
                        userId,
                        knownPlayerOnlineVersion,
                        SessionStreamWaitTimeout,
                        waitCancellation.Token);
                    Task completed = await Task.WhenAny(
                        ledgerWaitTask,
                        chatWaitTask,
                        worldConfigurationWaitTask,
                        playerOnlineWaitTask,
                        receiveTask);
                    waitCancellation.Cancel();
                    if (completed == receiveTask)
                    {
                        break;
                    }

                    if (completed == chatWaitTask)
                    {
                        chatWait = await chatWaitTask;
                        if (!chatWait.Changed)
                        {
                            continue;
                        }

                        knownChatVersion = chatWait.Version;
                        await SendSessionWebSocketEventAsync(
                            webSocket,
                            "chatChanged",
                            new ChatStreamEventDto(userId, knownChatVersion, T("SessionStream.ChatUpdated")),
                            sendGate,
                            cancellationToken);
                        continue;
                    }

                    if (completed == worldConfigurationWaitTask)
                    {
                        worldConfigurationWait = await worldConfigurationWaitTask;
                        if (!worldConfigurationWait.Changed)
                        {
                            continue;
                        }

                        knownWorldConfigurationVersion = worldConfigurationWait.Version;
                        await SendSessionWebSocketEventAsync(
                            webSocket,
                            "worldConfigurationChanged",
                            new WorldConfigurationStreamEventDto(userId, knownWorldConfigurationVersion, T("SessionStream.WorldCatalogUpdated")),
                            sendGate,
                            cancellationToken);
                        continue;
                    }

                    if (completed == playerOnlineWaitTask)
                    {
                        playerOnlineWait = await playerOnlineWaitTask;
                        if (!playerOnlineWait.Changed)
                        {
                            continue;
                        }

                        knownPlayerOnlineVersion = playerOnlineWait.Version;
                        foreach (PlayerOnlineNotificationRecord notification in playerOnlineWait.Notifications)
                        {
                            await SendSessionWebSocketEventAsync(
                                webSocket,
                                "playerOnline",
                                new PlayerOnlineStreamEventDto(
                                    userId,
                                    notification.OnlineUserId,
                                    notification.Version,
                                    null),
                                sendGate,
                                cancellationToken);
                        }

                        continue;
                    }

                    wait = await ledgerWaitTask;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    continue;
                }

                if (!wait.Changed)
                {
                    continue;
                }

                knownVersion = wait.Version;
                await SendSessionWebSocketEventAsync(
                    webSocket,
                    "ledgerChanged",
                    new SessionStreamEventDto(userId, online: true, knownVersion, T("SessionStream.EventLedgerUpdated")),
                    sendGate,
                    cancellationToken);
            }
        }
        finally
        {
            streamCancellation.Cancel();
            if (webSocket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Session stream closed.",
                        CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }

            if (receiveTask is not null)
            {
                try
                {
                    await receiveTask;
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
                {
                }
            }

            webSocket?.Dispose();
            state.LoginSessions.End(userId, sessionId);
            presence?.Dispose();
            RuntimeLogger(loggerFactory).LogInformation(
                "玩家离线：user={UserId} colony={ColonyId} session={SessionId} channel=ws",
                userId,
                colonyId,
                sessionId);
            RunClientLifecycleHooks(
                state,
                ClientLifecycleEvent.Disconnected(
                    userId,
                    colonyId,
                    sessionId,
                    DateTimeOffset.UtcNow));
        }
    }

    private static async Task RejectSessionStreamRequestAsync(
        HttpContext context,
        int statusCode,
        ProtocolErrorCode errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(
            new { result = ProtocolResponse.Reject(errorCode, message) },
            cancellationToken);
    }

    private static async Task ReceiveSessionWebSocketMessagesAsync(
        WebSocket webSocket,
        OnlinePresenceLease presence,
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string sessionId,
        SemaphoreSlim sendGate,
        CancellationTokenSource streamCancellation,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested
            && webSocket.State is WebSocketState.Open or WebSocketState.CloseSent or WebSocketState.CloseReceived)
        {
            string? message = await ReceiveSessionWebSocketTextAsync(webSocket, cancellationToken);
            if (message is null)
            {
                streamCancellation.Cancel();
                break;
            }

            string? eventName = TryReadSessionWebSocketEventName(message);
            if (!string.Equals(eventName, "ping", StringComparison.Ordinal))
            {
                continue;
            }

            if (!presence.Touch())
            {
                await SendSessionWebSocketEventAsync(
                    webSocket,
                    "error",
                    new SessionStreamEventDto(userId, online: false, state.EventNotifications.GetVersion(userId), T("SessionStream.LeaseExpired")),
                    sendGate,
                    cancellationToken);
                streamCancellation.Cancel();
                break;
            }

            state.LoginSessions.Refresh(userId, colonyId, sessionId, DateTimeOffset.UtcNow);
            if (!state.LoginSessions.IsValid(userId, colonyId, sessionId, DateTimeOffset.UtcNow))
            {
                await SendSessionWebSocketEventAsync(
                    webSocket,
                    "error",
                    new SessionStreamEventDto(userId, online: false, state.EventNotifications.GetVersion(userId), T("Auth.SessionExpired")),
                    sendGate,
                    cancellationToken);
                streamCancellation.Cancel();
                break;
            }

            await SendSessionWebSocketEventAsync(
                webSocket,
                "pong",
                new SessionStreamEventDto(userId, online: true, state.EventNotifications.GetVersion(userId), null),
                sendGate,
                cancellationToken);
        }
    }

    private static async Task<string?> ReceiveSessionWebSocketTextAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? TryReadSessionWebSocketEventName(string message)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("eventName", out JsonElement eventName)
                || root.TryGetProperty("event", out eventName)
                || root.TryGetProperty("type", out eventName))
            {
                return eventName.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static async Task SendSessionWebSocketEventAsync(
        WebSocket webSocket,
        string eventName,
        object payload,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        string data = JsonSerializer.Serialize(payload);
        string envelope = JsonSerializer.Serialize(new { eventName, data });
        byte[] bytes = Encoding.UTF8.GetBytes(envelope);
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static async Task<IResult> MaintainPresence(
        MaintainPresenceRequest request,
        ClashOfRimNetworkState state,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
        {
            return Results.Ok(new MaintainPresenceResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Presence.MissingIdentity")),
                request.UserId ?? string.Empty,
                online: false));
        }

        if (!state.LoginSessions.IsValid(request.UserId, request.ColonyId, request.SessionId, DateTimeOffset.UtcNow))
        {
            return Results.Ok(new MaintainPresenceResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Presence.InvalidSession")),
                request.UserId,
                online: false));
        }

        if (!state.OnlinePresence.TryConnectExclusive(request.UserId, out OnlinePresenceLease? presence))
        {
            RuntimeLogger(loggerFactory).LogWarning(
                "玩家上线被拒绝：user={UserId} colony={ColonyId} session={SessionId} reason=AlreadyOnline channel=presence",
                request.UserId,
                request.ColonyId,
                request.SessionId);
            return Results.Ok(new MaintainPresenceResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Presence.AlreadyOnline")),
                request.UserId,
                online: true));
        }

        using CancellationTokenSource presenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.ApplicationStopping);
        CancellationToken linkedCancellationToken = presenceCancellation.Token;

        try
        {
            RecordPlayerSeen(state, request.UserId, request.ColonyId, request.CurrentSnapshotId, DateTimeOffset.UtcNow);
            RuntimeLogger(loggerFactory).LogInformation(
                "玩家上线：user={UserId} colony={ColonyId} snapshot={SnapshotId} session={SessionId} channel=presence",
                request.UserId,
                request.ColonyId,
                request.CurrentSnapshotId,
                request.SessionId);
            try
            {
                while (!linkedCancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(SessionStreamWaitTimeout, linkedCancellationToken);
                    if (!presence!.Touch())
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (linkedCancellationToken.IsCancellationRequested)
            {
            }

            return Results.Ok(new MaintainPresenceResponse(
                ProtocolResponse.Ok(T("Presence.Closed")),
                request.UserId,
                online: false));
        }
        finally
        {
            state.LoginSessions.End(request.UserId, request.SessionId);
            presence?.Dispose();
            RuntimeLogger(loggerFactory).LogInformation(
                "玩家离线：user={UserId} colony={ColonyId} session={SessionId} channel=presence",
                request.UserId,
                request.ColonyId,
                request.SessionId);
            RunClientLifecycleHooks(
                state,
                ClientLifecycleEvent.Disconnected(
                    request.UserId,
                    request.ColonyId,
                    request.SessionId,
                    DateTimeOffset.UtcNow));
        }
    }

    private static IResult Logout(
        LogoutRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Results.Ok(new LogoutResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Presence.MissingIdentity")),
                request.UserId ?? string.Empty,
                disconnected: false));
        }

        state.LoginSessions.End(request.UserId, request.SessionId);
        bool disconnected = state.OnlinePresence.ForceDisconnect(request.UserId);
        RuntimeLogger(loggerFactory).LogInformation(
            "玩家请求登出：user={UserId} colony={ColonyId} session={SessionId} disconnected={Disconnected}",
            request.UserId,
            request.ColonyId,
            request.SessionId,
            disconnected);
        RunClientLifecycleHooks(
            state,
            ClientLifecycleEvent.Disconnected(
                request.UserId,
                request.ColonyId,
                request.SessionId,
                DateTimeOffset.UtcNow));

        return Results.Ok(new LogoutResponse(
            ProtocolResponse.Ok(T("Logout.Success")),
            request.UserId,
            disconnected));
    }

    private static IResult ListPlayers(ListPlayersRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
        {
            return Results.Ok(new ListPlayersResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Players.MissingIdentity")),
                Array.Empty<PlayerSummaryDto>()));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        RecordPlayerSeen(state, request.UserId, request.ColonyId, request.CurrentSnapshotId, nowUtc);
        LatestSnapshotLookup snapshotLookup = LatestSnapshotLookup.Build(state.SnapshotStore.ListLatest());
        List<PlayerSummaryDto> players = state.Players.List()
            .Select(record =>
            {
                string? currentSnapshotId = ResolveCurrentSnapshotId(snapshotLookup, record.UserId, record.ColonyId, record.CurrentSnapshotId);
                PlayerSnapshotWealthSummary snapshotSummary = IsSnapshotWealthCacheCurrent(record, currentSnapshotId)
                    ? new PlayerSnapshotWealthSummary(record.ColonyId, currentSnapshotId, record.LatestSnapshotWealth)
                    : CacheLatestSnapshotWealth(state, record.UserId, record.ColonyId, currentSnapshotId, nowUtc, snapshotLookup);
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
        RecordPlayerSeen(state, request.UserId, request.ColonyId, request.CurrentSnapshotId, nowUtc);
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

    private static void RecordPlayerSeen(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string? currentSnapshotId,
        DateTimeOffset seenAtUtc,
        string? displayName = null)
    {
        state.Players.Record(
            userId,
            colonyId,
            ResolveCurrentSnapshotId(state, userId, colonyId, currentSnapshotId),
            seenAtUtc,
            displayName);
    }

    private static string? ResolveCurrentSnapshotId(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string? currentSnapshotId)
    {
        LatestSnapshotRecord? exact = state.SnapshotStore.GetLatest(userId, colonyId);
        if (exact is not null)
        {
            return exact.Identity.SnapshotId;
        }

        LatestSnapshotRecord? byUser = ResolveLatestSnapshotForPlayer(state, userId, colonyId);
        if (byUser is not null)
        {
            return byUser.Identity.SnapshotId;
        }

        if (!string.IsNullOrWhiteSpace(currentSnapshotId))
        {
            return currentSnapshotId;
        }

        return null;
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
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return null;
        }

        LatestSnapshotRecord? latest = state.SnapshotStore.GetLatest(userId, colonyId);
        return string.Equals(latest?.Identity.SnapshotId, snapshotId, StringComparison.Ordinal)
            ? latest?.Envelope.NextLineageToken
            : null;
    }

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

    private static IResult PullPendingEvents(PullPendingEventsRequest request, ClashOfRimNetworkState state)
    {
        ReconcileExpiredDeliveredEvents(state, request.UserId, DateTimeOffset.UtcNow);
        ReconcileFailedSupportPawnEventsForDeletedActors(state, request.UserId);
        EventQueueSummary queue = EventQueueSummaryBuilder.BuildForTarget(
            request.UserId,
            state.Ledger.ListQueueForTarget(request.UserId));

        return Results.Ok(new PullPendingEventsResponse(
            ProtocolResponse.Ok(T("Events.QueueReturned")),
            ProtocolDtoMapper.ToDto(queue)));
    }

    private static async Task<IResult> WaitForEvents(
        WaitForEventsRequest request,
        ClashOfRimNetworkState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
        {
            EventQueueSummary emptyQueue = EventQueueSummaryBuilder.BuildForTarget(
                request.UserId ?? string.Empty,
                Array.Empty<AuthoritativeEvent>());
            return Results.Ok(new WaitForEventsResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.WaitMissingIdentity")),
                changed: false,
                notificationVersion: Math.Max(0, request.KnownNotificationVersion),
                ProtocolDtoMapper.ToDto(emptyQueue)));
        }

        EventNotificationWaitResult wait = await state.EventNotifications.WaitAsync(
            request.UserId,
            Math.Max(0, request.KnownNotificationVersion),
            TimeSpan.FromSeconds(request.TimeoutSeconds),
            cancellationToken);

        ReconcileExpiredDeliveredEvents(state, request.UserId, DateTimeOffset.UtcNow);
        ReconcileFailedSupportPawnEventsForDeletedActors(state, request.UserId);
        EventQueueSummary queue = EventQueueSummaryBuilder.BuildForTarget(
            request.UserId,
            state.Ledger.ListQueueForTarget(request.UserId));

        return Results.Ok(new WaitForEventsResponse(
            ProtocolResponse.Ok(wait.Changed ? T("Events.WaitChanged") : T("Events.WaitTimeout")),
            wait.Changed,
            wait.Version,
            ProtocolDtoMapper.ToDto(queue)));
    }

    private static IResult PullEventDetails(PullEventDetailsRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
        {
            return Results.Ok(new PullEventDetailsResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.DetailsMissingIdentity")),
                Array.Empty<EventDetailDto>()));
        }

        if (request.EventIds is null || request.EventIds.Count == 0)
        {
            return Results.Ok(new PullEventDetailsResponse(
                ProtocolResponse.Ok(T("Events.DetailsNoneRequested")),
                Array.Empty<EventDetailDto>()));
        }

        ReconcileExpiredDeliveredEvents(state, request.UserId, DateTimeOffset.UtcNow);
        ReconcileFailedSupportPawnEventsForDeletedActors(state, request.UserId);
        var details = new List<EventDetailDto>();
        foreach (string eventId in request.EventIds.Distinct(StringComparer.Ordinal))
        {
            AuthoritativeEvent? ledgerEvent = state.Ledger.Find(eventId);
            if (ledgerEvent is null || !IsVisibleTo(ledgerEvent, request.UserId, request.ColonyId))
            {
                return Results.Ok(new PullEventDetailsResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Events.NotFoundOrInvisible")),
                    Array.Empty<EventDetailDto>()));
            }

            bool visibleToTarget = IsVisibleParty(ledgerEvent.Target, request.UserId, request.ColonyId);
            if (visibleToTarget
                && IsSnapshotlessServerNotification(ledgerEvent)
                && ledgerEvent.Status is ServerEventStatus.PendingOfflineDelivery
                    or ServerEventStatus.ReadyForImmediateDelivery
                    or ServerEventStatus.DeliveredToClient)
            {
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                ledgerEvent = state.Ledger.MarkAccepted(
                    ledgerEvent.EventId,
                    nowUtc,
                    T("Events.ServerNotificationDelivered"));
            }
            else if (visibleToTarget
                && ledgerEvent.Status is ServerEventStatus.PendingOfflineDelivery or ServerEventStatus.ReadyForImmediateDelivery)
            {
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                ledgerEvent = state.Ledger.MarkDelivered(
                    ledgerEvent.EventId,
                    request.CurrentSnapshotId,
                    nowUtc);
            }

            details.Add(ProtocolDtoMapper.ToDetailDto(ledgerEvent));
            if (IsVisibleParty(ledgerEvent.Target, request.UserId, request.ColonyId)
                && IsReadOnlyTerminalEvent(ledgerEvent))
            {
                state.Ledger.ChangeStatus(ledgerEvent.EventId, ServerEventStatus.Cancelled);
            }
        }

        return Results.Ok(new PullEventDetailsResponse(
            ProtocolResponse.Ok(T("Events.DetailsReturned")),
            details));
    }

    private static bool IsReadOnlyTerminalEvent(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Status is ServerEventStatus.Conflict
            or ServerEventStatus.Failed
            or ServerEventStatus.RejectedByTarget;
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
        ReconcileExpiredRaidEvents(state, nowUtc);
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
            return Results.Ok(new UploadSnapshotResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure),
                acceptedSnapshotId: null,
                uploadResult: "AuthFailed"));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.UserId,
                request.ColonyId,
                nowUtc,
                out ProtocolResponse? pendingConfirmationFailure))
        {
            return Results.Ok(new UploadSnapshotResponse(
                pendingConfirmationFailure!,
                acceptedSnapshotId: null,
                uploadResult: "PendingConfirmationExpired"));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.UserId,
            request.ColonyId,
            request.SnapshotId,
            request.Package,
            multipart.Payload,
            nowUtc,
            confirmationOperation: request.ConfirmationOperation);
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
        RunSnapshotPostUploadProcessors(
            state,
            request.UserId,
            request.ColonyId,
            sessionId: null,
            upload,
            nowUtc,
            extraData: new SnapshotPostUploadExtraData(
                upload.SnapshotUploadKind,
                request.ConfirmationOperation,
                request.AchievementCandidates),
            registerPlayerColonySite: !string.Equals(
                request.ConfirmationOperation,
                SnapshotConfirmationOperations.ColonyRelocation,
                StringComparison.Ordinal),
            achievementCandidates: request.AchievementCandidates);

        return Results.Ok(new UploadSnapshotResponse(
            ToProtocolResponse(upload),
            upload.AcceptedSnapshot?.Identity.SnapshotId,
            upload.Kind.ToString(),
            upload.AcceptedSnapshot?.Envelope.NextLineageToken));
    }

    private static IResult DownloadLatestSnapshot(DownloadLatestSnapshotRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
        {
            return Results.Ok(new DownloadLatestSnapshotResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Snapshot.DownloadMissingIdentity")),
                snapshotId: null,
                package: null));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        ReconcileExpiredRaidEvents(state, nowUtc);
        if (!IsAuthorizedForColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                request.AuthorizationEventId,
                request.AuthorizationScope,
                nowUtc,
                out string authFailure))
        {
            return Results.Ok(new DownloadLatestSnapshotResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure),
                snapshotId: null,
                package: null));
        }

        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
        {
            return Results.Ok(new DownloadLatestSnapshotResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Snapshot.StoreDownloadUnsupported")),
                snapshotId: null,
                package: null));
        }

        SaveSnapshotPackage? package = packageStore.GetLatestPackage(request.UserId, request.ColonyId);
        if (package is null)
        {
            return Results.Ok(new DownloadLatestSnapshotResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Snapshot.LatestNotFound")),
                snapshotId: null,
                package: null));
        }

        package = CleanupSettledRaidBattleSnapshotBeforeDownload(
            state,
            packageStore,
            package,
            request.UserId,
            request.ColonyId,
            nowUtc);

        return Results.Ok(new DownloadLatestSnapshotResponse(
            ProtocolResponse.Ok(T("Snapshot.LatestReturned")),
            package.Envelope.Identity.SnapshotId,
            ProtocolDtoMapper.ToMetadataDto(package),
            BuildActiveRaidRecoveryDto(state, request.UserId, request.ColonyId, nowUtc)));
    }

    private static IResult DownloadLatestSnapshotPayload(DownloadLatestSnapshotPayloadRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.SnapshotId))
        {
            return Results.BadRequest(new
            {
                result = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Snapshot.PayloadDownloadMissingIdentity"))
            });
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!IsAuthorizedForColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                request.AuthorizationEventId,
                request.AuthorizationScope,
                nowUtc,
                out string authFailure))
        {
            return Results.Json(
                new
                {
                    result = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure)
                },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
        {
            return Results.Json(
                new
                {
                    result = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Snapshot.StoreDownloadUnsupported"))
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        SaveSnapshotPackage? package = packageStore.GetLatestPackage(request.UserId, request.ColonyId);
        if (package is not null)
        {
            package = CleanupSettledRaidBattleSnapshotBeforeDownload(
                state,
                packageStore,
                package,
                request.UserId,
                request.ColonyId,
                nowUtc);
        }

        if (package is null
            || !string.Equals(package.Envelope.Identity.SnapshotId, request.SnapshotId, StringComparison.Ordinal))
        {
            return Results.Json(
                new
                {
                    result = ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Snapshot.PayloadNotFound"))
                },
                statusCode: StatusCodes.Status404NotFound);
        }

        string fileName = (package.Envelope.Identity.SnapshotId ?? "latest") + ".payload";
        return Results.File(package.Payload, "application/octet-stream", fileName);
    }

    private static SaveSnapshotPackage CleanupSettledRaidBattleSnapshotBeforeDownload(
        ClashOfRimNetworkState state,
        IColonySnapshotPackageStore packageStore,
        SaveSnapshotPackage package,
        string userId,
        string colonyId,
        DateTimeOffset nowUtc)
    {
        if (FindActiveSourceRaidForAttacker(state, userId, colonyId) is not null)
        {
            return package;
        }

        string? raidEventId = package.Index.WorldObjects
            .Where(worldObject => string.Equals(worldObject.Def, "ClashOfRim_RemoteRaidBattleMapParent", StringComparison.Ordinal))
            .Select(worldObject => worldObject.ClashOfRimRelatedEventId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        AuthoritativeEvent? raid = string.IsNullOrWhiteSpace(raidEventId)
            ? null
            : state.Ledger.Find(raidEventId!);
        if (string.IsNullOrWhiteSpace(raidEventId) || raid?.Payload is not RaidEventPayload raidPayload)
        {
            return package;
        }

        string cleanedSnapshotId = BuildTimedOutRaidCleanupSnapshotId(colonyId, raidEventId, nowUtc);
        try
        {
            if (!RaidAttackerSnapshotCleanupEditor.TryRemoveRaidBattleState(
                    package,
                    cleanedSnapshotId,
                    nowUtc,
                    out SaveSnapshotPackage cleaned,
                    out RaidAttackerSnapshotCleanupResult cleanupResult))
            {
                return package;
            }

            packageStore.StoreLatest(cleaned, cleaned.Index, nowUtc);
            RecordLatestSnapshotReference(
                state,
                userId,
                colonyId,
                new LatestSnapshotRecord(cleaned.Envelope.Identity, cleaned.Envelope, cleaned.Index, nowUtc),
                nowUtc);
            var attackerLossEvents = new List<AuthoritativeEvent>();
            var supportLossNotifications = new List<AuthoritativeEvent>();
            AppendRaidCleanupLossEvents(
                state,
                raid,
                raidPayload,
                cleaned,
                cleanupResult,
                nowUtc,
                attackerLossEvents,
                supportLossNotifications,
                "map-cleanup");
            IReadOnlyList<string> usersToSignal = attackerLossEvents
                .Select(evt => evt.Target.UserId)
                .Concat(supportLossNotifications.Select(evt => evt.Target.UserId))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            state.EventNotifications.SignalUsers(usersToSignal);
            state.RuntimeLogger.LogInformation(
                "Cleaned stale raid battle snapshot before download: user={UserId} colony={ColonyId} sourceSnapshot={SourceSnapshotId} cleanedSnapshot={CleanedSnapshotId} removedMaps={RemovedMapCount} lostAttackPawns={LostAttackPawns} lostSupportPawns={LostSupportPawns}",
                userId,
                colonyId,
                package.Envelope.Identity.SnapshotId,
                cleaned.Envelope.Identity.SnapshotId,
                cleanupResult.RemovedMapCount,
                cleanupResult.LostAttackPawns.Count,
                cleanupResult.LostSupportPawns.Count);
            return cleaned;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or InvalidOperationException)
        {
            state.RuntimeLogger.LogWarning(
                ex,
                "Failed to clean stale raid battle snapshot before download: user={UserId} colony={ColonyId} sourceSnapshot={SourceSnapshotId}",
                userId,
                colonyId,
                package.Envelope.Identity.SnapshotId);
            return package;
        }
    }
}
