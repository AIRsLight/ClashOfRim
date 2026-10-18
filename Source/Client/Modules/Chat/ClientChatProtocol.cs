using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Chat;

internal interface IClientChatTransport
{
    Task<ClashOfRimClientNetworkResult<ModSendChatMessageResponseDto>> SendAsync(ClashOfRimClientNetworkContext context, string? target, string text, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModListChatMessagesResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, long after, int limit, CancellationToken token);
}

internal sealed class ClientChatProtocol : IClientChatTransport
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;
    internal ClientChatProtocol(ClientProtocolTransport transport, Func<string> notConfigured)
    { this.transport = transport; this.notConfigured = notConfigured; }
    public Task<ClashOfRimClientNetworkResult<ModSendChatMessageResponseDto>> SendAsync(ClashOfRimClientNetworkContext context, string? target, string text, CancellationToken token)
        => !context.IsConfigured ? Missing<ModSendChatMessageResponseDto>() : transport.ForContext(context).PostAsync<ModSendChatMessageRequestDto, ModSendChatMessageResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.SendChatMessage).Route,
            new() { UserId = context.UserId, ColonyId = context.ColonyId, AuthToken = context.AuthToken, TargetUserId = target, Text = text }, token);
    public Task<ClashOfRimClientNetworkResult<ModListChatMessagesResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, long after, int limit, CancellationToken token)
        => !context.IsConfigured ? Missing<ModListChatMessagesResponseDto>() : transport.ForContext(context).PostAsync<ModListChatMessagesRequestDto, ModListChatMessagesResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ListChatMessages).Route,
            new() { UserId = context.UserId, ColonyId = context.ColonyId, AuthToken = context.AuthToken, AfterSequence = Math.Max(0, after), Limit = limit }, token);
    private Task<ClashOfRimClientNetworkResult<T>> Missing<T>() => Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfigured()));
}
