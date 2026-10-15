using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

internal static class GiftRejectionTransportTests
{
    public static void Run() => Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        ClientSessionWork.Reset();
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var context = new ClashOfRimClientNetworkContext("http://127.0.0.1:12345", "recipient", "colony", "base", null, null, "recipient-token");
        var client = new ClashOfRimModNetworkClient(http, context);
        var result = await client.RejectGiftAsync("gift:v2~003A", "不需要");
        if (!result.Success || result.Response?.Result?.Accepted != true || handler.Calls != 1)
            throw new Exception("Gift rejection did not complete through the normal client transport.");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != ProtocolContractManifest.Find(ProtocolMessageKind.RejectGift).Route)
                throw new Exception("Unexpected gift rejection route.");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            foreach (var (field, expected) in new[] { ("eventId", "gift:v2~003A"), ("userId", "recipient"),
                ("colonyId", "colony"), ("currentSnapshotId", "base"), ("reason", "不需要"), ("authToken", "recipient-token") })
                if (json.RootElement.GetProperty(field).GetString() != expected) throw new Exception("Gift rejection wire field mismatch: " + field);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true},\"eventId\":\"gift:v2~003A\",\"returnEventId\":\"return\",\"returnEventCreated\":true}") };
        }
    }
}
