using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;

internal static class RetiredGiftEndpointTests
{
    public static async Task RunAsync(HttpClient http, ClashOfRimNetworkState state, string authToken, string existingKey)
    {
        string ledgerBefore = JsonSerializer.Serialize(state.Ledger.ListAll());
        string snapshotBefore = JsonSerializer.Serialize(state.SnapshotStore.GetLatest("user-a", "colony-a"));
        var versions = new[] { "user-a", "user-b" }.ToDictionary(u => u, state.EventNotifications.GetVersion);
        foreach (string key in new[] { "retired-gift:new", existingKey })
        foreach (string? token in new[] { null, "invalid-token", authToken })
        foreach (string? kind in new[] { null, "Forced" })
        {
            var gift = new CreateGiftRequest(key, new ProtocolIdentity("user-a", "colony-a", "old-snapshot"),
                new ProtocolIdentity("user-b", "colony-b", null),
                new[] { new ThingReferenceDto("owner:user-a/colony:colony-a/thing:silver", "Silver", 100) }, "retired", deliveryKind: kind);
            using var request = new HttpRequestMessage(HttpMethod.Post, ProtocolContractManifest.Find(ProtocolMessageKind.CreateGift).Route)
                { Content = JsonContent.Create(gift) };
            if (token != null) request.Headers.Add(MultipartSnapshotTransport.AuthenticationHeaderName, token);
            using var response = await http.SendAsync(request);
            await Rejected(response);
        }
        // No request binding: even absent or malformed payloads get a migration response, never a write.
        foreach (string body in new[] { "", "null", "{}", "not-json" })
        {
            using var response = await http.PostAsync(ProtocolContractManifest.Find(ProtocolMessageKind.CreateGift).Route,
                new StringContent(body, Encoding.UTF8, "application/json"));
            await Rejected(response);
        }
        if (ledgerBefore != JsonSerializer.Serialize(state.Ledger.ListAll())
            || snapshotBefore != JsonSerializer.Serialize(state.SnapshotStore.GetLatest("user-a", "colony-a"))
            || versions.Any(p => state.EventNotifications.GetVersion(p.Key) != p.Value))
            throw new Exception("Retired gift endpoint changed the ledger, snapshot or notifications.");
        Console.WriteLine("通过：退役礼物入口拒绝新建、重复、普通、强投及各种认证请求且不修改状态");
    }

    private static async Task Rejected(HttpResponseMessage response)
    {
        var result = await response.Content.ReadFromJsonAsync<EventCreationResponse>();
        if (response.StatusCode != HttpStatusCode.OK || result?.Result.Accepted != false
            || result.Result.ErrorCode != ProtocolErrorCode.ServerRejected || result.EventId != null
            || result.AppliedSnapshotId != null || string.IsNullOrWhiteSpace(result.Result.Message))
            throw new Exception("Retired gift endpoint did not return an explicit non-success migration response.");
    }
}
