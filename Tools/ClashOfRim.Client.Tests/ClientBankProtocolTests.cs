using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientBankProtocolTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool facade in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                var context = new ClashOfRimClientNetworkContext("http://owner", "alice", "colony", "base", null, null, "auth");
                using var handler = new Handler(); using var http = new HttpClient(handler);
                var protocol = new ClientBankProtocol(context, new ClientProtocolTransport(http,
                    new("http://wrong", "wrong", "wrong", "wrong", null, null, "wrong"), new Environment()), () => "not configured");
                var client = new ClashOfRimModNetworkClient(http, context);
                handler.Expected = ProtocolMessageKind.GetBankStatus;
                var status = facade ? await client.GetBankStatusAsync(123, 10000) : await protocol.GetBankStatusAsync(123, 10000);
                Require(status.Success, "Bank status codec failed.");
                foreach (bool snapshot in new[] { false, true })
                foreach (ClientBankOperation kind in Enum.GetValues<ClientBankOperation>())
                {
                    string method = kind switch { ClientBankOperation.Borrow => "CreateBankLoan", ClientBankOperation.RepayLoan => "RepayBankLoan", _ => "RepayBankDebt" };
                    handler.Expected = Enum.Parse<ProtocolMessageKind>(method + (snapshot ? "WithSnapshot" : ""));
                    handler.SnapshotKind = kind switch { ClientBankOperation.Borrow => ModSnapshotUploadKinds.BankLoanCreationConfirmation,
                        ClientBankOperation.RepayLoan => ModSnapshotUploadKinds.BankLoanRepaymentConfirmation, _ => ModSnapshotUploadKinds.BankDebtRepaymentConfirmation };
                    var metadata = new ModSnapshotPackageMetadataDto { SnapshotId = "prepared" };
                    byte[] payload = Encoding.UTF8.GetBytes("snapshot");
                    if (kind == ClientBankOperation.Borrow)
                    {
                        var result = snapshot
                            ? facade ? await client.CreateBankLoanWithSnapshotAsync("key", 123, 10000, 100, 2, "loan", 1, metadata, payload)
                                : await protocol.CreateBankLoanWithSnapshotAsync("key", 123, 10000, 100, 2, "loan", 1, metadata, payload)
                            : facade ? await client.CreateBankLoanAsync("key", 123, 10000, 100, 2) : await protocol.CreateBankLoanAsync("key", 123, 10000, 100, 2);
                        Require(result.Success && result.Response!.Loan?.LoanId == "loan", "Borrow codec failed.");
                    }
                    else if (kind == ClientBankOperation.RepayLoan)
                    {
                        var result = snapshot
                            ? facade ? await client.RepayBankLoanWithSnapshotAsync("key", 123, "loan", 110, metadata, payload)
                                : await protocol.RepayBankLoanWithSnapshotAsync("key", 123, "loan", 110, metadata, payload)
                            : facade ? await client.RepayBankLoanAsync("key", 123, 110) : await protocol.RepayBankLoanAsync("key", 123, 110);
                        Require(result.Success && result.Response!.AppliedSnapshotId == "accepted", "Loan repayment codec failed.");
                    }
                    else
                    {
                        var result = snapshot
                            ? facade ? await client.RepayBankDebtWithSnapshotAsync("key", 123, "debt", 110, metadata, payload)
                                : await protocol.RepayBankDebtWithSnapshotAsync("key", 123, "debt", 110, metadata, payload)
                            : facade ? await client.RepayBankDebtAsync("key", 123, "debt", 110) : await protocol.RepayBankDebtAsync("key", 123, "debt", 110);
                        Require(result.Success && result.Response!.Debt?.DebtId == "debt", "Debt repayment codec failed.");
                    }
                }
                Require(handler.Calls == 7, "Not all bank routes were exercised.");
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal ProtocolMessageKind Expected;
        internal string? SnapshotKind;
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Require(request.RequestUri!.Host == "owner" && request.RequestUri.AbsolutePath == ProtocolContractManifest.Find(Expected).Route,
                "Bank codec borrowed another transport context or route.");
            bool snapshot = Expected.ToString().EndsWith("WithSnapshot", StringComparison.Ordinal);
            string json;
            if (snapshot)
            {
                Require(request.Headers.GetValues(MultipartSnapshotTransport.AuthenticationHeaderName).Single() == "auth", "Multipart lost captured credentials.");
                var form = (MultipartFormDataContent)request.Content!;
                json = await form.Single(c => c.Headers.ContentDisposition!.Name!.Trim('"') == "request").ReadAsStringAsync();
            }
            else json = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            Require(root.GetProperty("userId").GetString() == "alice" && root.GetProperty("colonyId").GetString() == "colony"
                && root.GetProperty("currentSnapshotId").GetString() == "base" && root.GetProperty("authToken").GetString() == "auth"
                && root.GetProperty("currentGameTicks").GetInt64() == 123, "Bank identity or game time changed on the wire.");
            if (Expected != ProtocolMessageKind.GetBankStatus) Require(root.GetProperty("idempotencyKey").GetString() == "key", "Bank key changed.");
            if (snapshot) Require(root.GetProperty("confirmedSnapshot").GetProperty("snapshotUploadKind").GetString() == SnapshotKind,
                "Wrong bank snapshot semantic kind.");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true},\"loan\":{\"loanId\":\"loan\"},\"debt\":{\"debtId\":\"debt\"},\"appliedSnapshotId\":\"accepted\"}", Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Environment : IClientProtocolEnvironment
    {
        public string? CurrentLanguage => "English";
        public string DeserializationFailure(string type) => type;
        public Action? CaptureSessionUnavailable(AIRsLight.ClashOfRim.ClientNetwork.ClashOfRimClientNetworkContext context, string? sessionId) => null;
        public void MultipartDisposalFailed(string message) { }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
