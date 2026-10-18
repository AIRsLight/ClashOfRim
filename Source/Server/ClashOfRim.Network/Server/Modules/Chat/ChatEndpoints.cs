using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Chat;

internal static class ChatEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, ChatService chat)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SendChatMessage).Route,
            (SendChatMessageRequest request) => Results.Ok(chat.Send(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListChatMessages).Route,
            (ListChatMessagesRequest request) => Results.Ok(chat.List(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
    }
}
