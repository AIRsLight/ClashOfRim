using System.Runtime.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

internal static class WireContractTests
{
    private static readonly JsonSerializerOptions ServerJson = new(JsonSerializerDefaults.Web);
    public static void Run()
    {
        var snapshot = new ModSnapshotPackageMetadataDto
        {
            PackageVersion = "fixture", OwnerId = "玩家", ColonyId = "colony", SnapshotId = "after",
            RimWorldVersion = "1.7", PayloadEncoding = "GzipRws", PayloadBytes = 123, OriginalSaveBytes = 456,
            PayloadSha256 = "payload", OriginalSha256 = "original", PreviousSnapshotId = "before",
            LineageToken = "lineage", NextLineageToken = null, GameTicks = 12345,
            SnapshotUploadKind = ModSnapshotUploadKinds.BankLoanRepaymentConfirmation, DefenderThreatPoints = 42.5f
        };
        RoundTrip<ModSnapshotPackageMetadataDto, SnapshotPackageMetadataDto>(snapshot);
        RoundTrip<ModRepayBankLoanWithSnapshotRequestDto, RepayBankLoanWithSnapshotRequest>(new()
        {
            UserId = "玩家", ColonyId = "colony", AuthToken = "token", IdempotencyKey = "key",
            CurrentSnapshotId = "before", CurrentGameTicks = 12345, LoanId = "loan", SilverPaid = 110, ConfirmedSnapshot = snapshot
        });
        RoundTrip<ModCreateBankLoanWithSnapshotRequestDto, CreateBankLoanWithSnapshotRequest>(new()
        {
            UserId = "玩家", ColonyId = "colony", AuthToken = null, IdempotencyKey = "borrow",
            CurrentSnapshotId = "before", CurrentGameTicks = 12345, ColonyWealth = 10000,
            PrincipalSilver = 100, DurationDays = 1, ExpectedInterestSilver = 10, RequestedLoanId = "loan", ConfirmedSnapshot = snapshot
        });
        RoundTrip<ModRepayBankDebtWithSnapshotRequestDto, RepayBankDebtWithSnapshotRequest>(new()
        {
            UserId = "玩家", ColonyId = "colony", AuthToken = "token", IdempotencyKey = "debt-payment",
            CurrentSnapshotId = "before", CurrentGameTicks = 12345, DebtId = "debt", SilverPaid = 100, ConfirmedSnapshot = snapshot
        });
        RoundTrip<ModSendChatMessageRequestDto, SendChatMessageRequest>(new()
        {
            UserId = "玩家", ColonyId = "colony", AuthToken = null, TargetUserId = "friend", Text = "你好\nworld"
        });
        RoundTrip<ModRejectGiftRequestDto, RejectGiftRequest>(new()
        {
            EventId = "gift:v2~003A", UserId = "玩家", ColonyId = "colony", CurrentSnapshotId = "snapshot",
            Reason = "拒收", AuthToken = "gift-token"
        });
        RoundTrip<ModRejectGiftRequestDto, RejectGiftRequest>(new()
        {
            EventId = "legacy-gift", UserId = "玩家", ColonyId = "colony", CurrentSnapshotId = "snapshot", AuthToken = null
        });
        var response = new ListChatMessagesResponse(ProtocolResponse.Ok(),
            new[] { new ChatMessageDto(42, "message", "private", "玩家", "colony", null, "你好", DateTimeOffset.UnixEpoch) }, 42);
        byte[] serverBytes = JsonSerializer.SerializeToUtf8Bytes(response, ServerJson);
        var serializer = Serializer<ModListChatMessagesResponseDto>();
        using var stream = new MemoryStream(serverBytes);
        var client = (ModListChatMessagesResponseDto)serializer.ReadObject(stream)!;
        if (client.Messages.Count != 1 || client.Messages[0].Sequence != 42 || client.Messages[0].Text != "你好"
            || client.Result?.Accepted != true || client.LatestSequence != 42)
            throw new Exception("Server chat response contract drifted.");
    }

    private static DataContractJsonSerializer Serializer<T>() => new(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
    private static void RoundTrip<TClient, TServer>(TClient value)
    {
        var serializer = Serializer<TClient>();
        using var original = new MemoryStream();
        serializer.WriteObject(original, value);
        TServer server = JsonSerializer.Deserialize<TServer>(original.ToArray(), ServerJson)!;
        using var received = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(server, ServerJson));
        var client = (TClient)serializer.ReadObject(received)!;
        using var returned = new MemoryStream();
        serializer.WriteObject(returned, client);
        if (!JsonNode.DeepEquals(JsonNode.Parse(original.ToArray()), JsonNode.Parse(returned.ToArray())))
            throw new Exception("Wire contract drift: " + typeof(TClient).Name);
    }
}
