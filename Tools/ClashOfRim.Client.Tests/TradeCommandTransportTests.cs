using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

internal static class TradeCommandTransportTests
{
    public static void Run() => Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        ClientSessionWork.Reset();
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var context = new ClashOfRimClientNetworkContext("http://127.0.0.1:12345", "trader", "colony", "base", null, null, "trade-token");
        var client = new ClashOfRimModNetworkClient(http, context);
        var accepted = await client.AcceptTradeOrderAsync("key", "order", true);
        var cancelled = await client.CancelTradeOrderAsync("key", "order", "取消");
        if (!accepted.Success || accepted.Response?.Result?.Accepted != true
            || !cancelled.Success || cancelled.Response?.Result?.Accepted != true || handler.Calls != 2)
            throw new Exception("Trade command transport failed.");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool accepting = Calls++ == 0;
            var kind = accepting ? ProtocolMessageKind.AcceptTradeOrder : ProtocolMessageKind.CancelTradeOrder;
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != ProtocolContractManifest.Find(kind).Route)
                throw new Exception("Unexpected trade command route.");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = json.RootElement;
            var party = body.GetProperty(accepting ? "acceptor" : "owner");
            if (body.GetProperty("authToken").GetString() != "trade-token" || body.GetProperty("tradeEventId").GetString() != "order"
                || body.GetProperty("idempotencyKey").GetString() != "key" || party.GetProperty("userId").GetString() != "trader"
                || party.GetProperty("colonyId").GetString() != "colony" || party.GetProperty("snapshotId").GetString() != "base"
                || (accepting ? !body.GetProperty("postagePaidByAcceptor").GetBoolean() : body.GetProperty("reason").GetString() != "取消"))
                throw new Exception("Trade command request body mismatch.");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true},\"tradeEventId\":\"order\"}") };
        }
    }
}
