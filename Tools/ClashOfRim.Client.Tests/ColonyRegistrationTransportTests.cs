using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

internal static class ColonyRegistrationTransportTests
{
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (bool suppress in new[] { false, true })
                foreach (string? token in new[] { null, "current-session-token" })
                    Verify(token, suppress).GetAwaiter().GetResult();
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static async Task Verify(string? token, bool suppress)
    {
        ClientSessionWork.Reset();
        using var handler = new Handler(token, suppress);
        using var http = new HttpClient(handler);
        var context = new ClashOfRimClientNetworkContext("http://127.0.0.1:12345", "玩家I", "colony", null, null, "password", token);
        var client = new ClashOfRimModNetworkClient(http, context);
        var result = await client.RegisterPlayerColonySitesAsync(new[] { new ModPlayerColonySiteDto
        { UserId = "玩家I", ColonyId = "colony", WorldObjectId = "object", MapUniqueId = "Map_23", Tile = 0, TileLayerId = 2, Label = "新家" } },
            new[] { new ModWorldConfigurationExtensionDto { ProviderId = "provider", Kind = "kind", SchemaVersion = "1", PayloadJson = "{}" } }, suppress);
        if (!result.Success || result.Response?.Result?.Accepted != (token is not null) || handler.Calls != 1 || context.AuthToken != token)
            throw new Exception($"Registration transport changed acceptance or session ownership: calls={handler.Calls}, success={result.Success}, accepted={result.Response?.Result?.Accepted}, error={result.ErrorCode}, message={result.Message}.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly string? token;
        private readonly bool suppress;
        internal Handler(string? token, bool suppress) { this.token = token; this.suppress = suppress; }
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != ProtocolContractManifest.Find(ProtocolMessageKind.RegisterPlayerColonySites).Route)
                throw new Exception("Unexpected registration route.");
            // Deserialize the actual mod transport JSON into the server's immutable protocol DTO.
            var value = JsonSerializer.Deserialize<RegisterPlayerColonySitesRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (value.ProtocolVersion != ProtocolApiVersion.Current || value.UserId != "玩家I" || value.ColonyId != "colony"
                || value.AuthToken != token || value.SuppressWorldConfigurationNotification != suppress || value.Sites.Count != 1
                || value.Sites[0] is not { Tile: 0, TileLayerId: 2, MapUniqueId: "Map_23", Label: "新家" }
                || value.Extensions.Count != 1 || value.Extensions[0].PayloadJson != "{}")
                throw new Exception("Registration JSON lost its credential or business fields.");
            var response = new RegisterPlayerColonySitesResponse(token is null
                ? ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, "denied") : ProtocolResponse.Ok("accepted"),
                token is null ? 0 : 1, null, null);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        }
    }
}
