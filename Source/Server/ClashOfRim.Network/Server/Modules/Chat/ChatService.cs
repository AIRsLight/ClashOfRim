using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Chat;

internal sealed class ChatService(ColonyAccessAuthorization authorization, SnapshotMutationCoordinator mutations,
    ChatMessageRegistry messages, PlayerRegistry players, EventNotificationHub notifications)
{
    internal SendChatMessageResponse Send(SendChatMessageRequest request)
    {
        SendChatMessageResponse Reject(ProtocolResponse failure) => new(failure, null);
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Chat.SendMissingIdentity")));

        // Serialize authorization and publication with session retirement; chat has no snapshot recovery prerequisite.
        using var mutation = mutations.EnterUnrecovered();
        if (Authorize(request.AuthToken, request.UserId, request.ColonyId) is { } denied) return Reject(denied);
        if (string.IsNullOrWhiteSpace(request.Text))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Chat.EmptyContent")));
        string? target = string.IsNullOrWhiteSpace(request.TargetUserId) ? null : request.TargetUserId.Trim();
        if (target is not null && !players.ContainsUser(target))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerLocalization.Text("Chat.PrivateTargetNotFound")));

        var record = messages.Add(request.UserId, request.ColonyId, target, request.Text, DateTimeOffset.UtcNow);
        notifications.SignalUsers(target is null ? players.List().Select(player => player.UserId) : new[] { request.UserId, target });
        return new(ProtocolResponse.Ok(ServerLocalization.Text("Chat.Sent")), ToDto(record));
    }

    internal ListChatMessagesResponse List(ListChatMessagesRequest request)
    {
        long cursor = Math.Max(0, request.AfterSequence);
        ListChatMessagesResponse Reject(ProtocolResponse failure) => new(failure, [], cursor);
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Chat.ListMissingIdentity")));

        using var mutation = mutations.EnterUnrecovered();
        if (Authorize(request.AuthToken, request.UserId, request.ColonyId) is { } denied) return Reject(denied);
        var visible = messages.ListVisible(request.UserId, request.AfterSequence, request.Limit).Select(ToDto).ToArray();
        return new(ProtocolResponse.Ok(ServerLocalization.Text("Chat.Returned")), visible,
            visible.Length == 0 ? cursor : visible[^1].Sequence);
    }

    private ProtocolResponse? Authorize(string? token, string userId, string colonyId)
        => authorization.TryAuthorize(token, userId, colonyId, null, null, DateTimeOffset.UtcNow, out var failure)
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, failure);

    private static ChatMessageDto ToDto(ChatMessageRecord message)
        => new(message.Sequence, message.MessageId, message.Channel, message.FromUserId, message.FromColonyId,
            message.TargetUserId, message.Text, message.SentAtUtc);
}
