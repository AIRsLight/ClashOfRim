using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

internal static class SupportClosureTransportTests
{
    public static void Run() => Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        ClientSessionWork.Reset();
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var context = new ClashOfRimClientNetworkContext("http://127.0.0.1:12345", "recipient", "colony", "base", null, null, "support-token");
        var client = new ClashOfRimModNetworkClient(http, context);
        var rejection = await client.RejectSupportPawnAsync("support", "拒绝");
        var completion = await client.FinishSupportPawnAsync("finish", "support", "Expired", "pawn", "支援者", true, null);
        if (!rejection.Success || rejection.Response?.Result?.Accepted != true
            || !completion.Success || completion.Response?.Result?.Accepted != true || handler.Calls != 2)
            throw new Exception("Support closure client transport failed.");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var kind = Calls++ == 0 ? ProtocolMessageKind.RejectSupportPawn : ProtocolMessageKind.FinishSupportPawn;
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != ProtocolContractManifest.Find(kind).Route)
                throw new Exception("Unexpected support closure route.");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var data = json.RootElement;
            foreach (var (field, expected) in new[] { ("eventId", "support"), ("userId", "recipient"), ("colonyId", "colony"),
                ("currentSnapshotId", "base"), ("authToken", "support-token") })
                if (data.GetProperty(field).GetString() != expected) throw new Exception("Support closure field mismatch: " + field);
            if (kind == ProtocolMessageKind.RejectSupportPawn && data.GetProperty("reason").GetString() != "拒绝"
                || kind == ProtocolMessageKind.FinishSupportPawn && (data.GetProperty("idempotencyKey").GetString() != "finish"
                    || data.GetProperty("finishReason").GetString() != "Expired" || !data.GetProperty("pawnDead").GetBoolean()
                    || data.GetProperty("pawnGlobalKey").GetString() != "pawn" || data.GetProperty("pawnName").GetString() != "支援者"))
                throw new Exception("Support closure request body mismatch.");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true},\"eventId\":\"support\",\"returnEventId\":\"return\",\"created\":true,\"returnEventCreated\":true}") };
        }
    }
}
