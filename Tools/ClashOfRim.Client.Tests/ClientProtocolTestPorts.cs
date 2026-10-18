using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Transport;

internal static class ClientProtocolTestPorts
{
    internal static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    internal sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    internal sealed class Environment : IClientProtocolEnvironment
    {
        public string CurrentLanguage => "简体中文 İ";
        public string DeserializationFailure(string type) => type;
        public Action? CaptureSessionUnavailable(AIRsLight.ClashOfRim.ClientNetwork.ClashOfRimClientNetworkContext context, string? sessionId) => () => throw new InvalidOperationException("Unexpected session expiry.");
        public void MultipartDisposalFailed(string message) => throw new InvalidOperationException("Unexpected multipart.");
    }
}
