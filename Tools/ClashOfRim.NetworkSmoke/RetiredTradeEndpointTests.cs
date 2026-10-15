using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;

internal static class RetiredTradeEndpointTests
{
    public static async Task RunAsync(HttpClient http, ClashOfRimNetworkState state, string ownerToken, string buyerToken,
        string orderId, string memoId, string createKey, string fulfillmentKey)
    {
        string ledger = JsonSerializer.Serialize(state.Ledger.ListAll()), snapshots = JsonSerializer.Serialize(state.SnapshotStore.ListLatest());
        var versions = new[] { "user-a", "user-b", "user-orbit", "user-surface" }.ToDictionary(u => u, state.EventNotifications.GetVersion);
        foreach (bool fulfillment in new[] { false, true })
        {
            string route = ProtocolContractManifest.Find(fulfillment ? ProtocolMessageKind.FulfillTradeOrder : ProtocolMessageKind.CreateTradeOrder).Route;
            foreach (string key in new[] { fulfillment ? fulfillmentKey : createKey, "retired-trade:new" })
            foreach (string? token in new[] { null, "invalid-token", ownerToken, buyerToken })
            foreach (string mode in fulfillment ? new[] { "SelfDelivery", "ServerDropPod" } : new[] { "create" })
            {
                object request = fulfillment
                    ? new FulfillTradeOrderRequest(key, orderId, memoId, new ProtocolIdentity("user-b", "colony-b", "old"),
                        new[] { new ThingReferenceDto("paid", "ComponentIndustrial", 2) }, mode)
                    : new CreateTradeOrderRequest(key, new ProtocolIdentity("user-a", "colony-a", "old"),
                        new[] { new ThingReferenceDto("offered", "Silver", 1) }, Array.Empty<ThingReferenceDto>(), 10, true, true);
                using var message = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(request, request.GetType()) };
                if (token != null) message.Headers.Add(MultipartSnapshotTransport.AuthenticationHeaderName, token);
                using var response = await http.SendAsync(message);
                await Rejected(response, fulfillment);
            }
            foreach (string body in new[] { "", "null", "{}", "not-json" })
            {
                using var response = await http.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"));
                await Rejected(response, fulfillment);
            }
        }
        if (ledger != JsonSerializer.Serialize(state.Ledger.ListAll()) || snapshots != JsonSerializer.Serialize(state.SnapshotStore.ListLatest())
            || versions.Any(p => state.EventNotifications.GetVersion(p.Key) != p.Value))
            throw new Exception("Retired trade endpoints changed the ledger, snapshots or notifications.");
        Console.WriteLine("通过：退役交易入口拒绝新建、重试、各种认证和畸形正文且不产生经济变更");
    }

    private static async Task Rejected(HttpResponseMessage response, bool fulfillment)
    {
        bool rejected;
        if (fulfillment)
        {
            var value = await response.Content.ReadFromJsonAsync<FulfillTradeOrderResponse>();
            rejected = value?.Result.Accepted == false && value.Result.ErrorCode == ProtocolErrorCode.ServerRejected
                && !string.IsNullOrWhiteSpace(value.Result.Message) && value.TradeEventId == null && value.AcceptedMemoEventId == null
                && value.ExchangeEventId == null && !value.ExchangeCreated && value.ReceivedThings.Count == 0
                && value.OwnerDeliveryEventId == null && value.AcceptorDeliveryEventId == null && value.AppliedSnapshotId == null && value.NextLineageToken == null;
        }
        else
        {
            var value = await response.Content.ReadFromJsonAsync<EventCreationResponse>();
            rejected = value?.Result.Accepted == false && value.Result.ErrorCode == ProtocolErrorCode.ServerRejected
                && !string.IsNullOrWhiteSpace(value.Result.Message) && value.EventId == null && value.AppliedSnapshotId == null && value.NextLineageToken == null;
        }
        if (response.StatusCode != HttpStatusCode.OK || !rejected)
            throw new Exception("Retired trade endpoint accepted or disclosed an operation: " + (fulfillment ? "fulfillment" : "creation"));
    }
}
