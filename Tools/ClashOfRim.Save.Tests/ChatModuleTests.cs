using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Chat;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class ChatModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        int count = 0;
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (bool send in new[] { false, true })
                {
                    foreach (string mode in new[] { "missing-user", "missing-colony", "no-token", "bad-token", "wrong-user", "wrong-colony", "case", "expired-token", "expired-session", "replaced", "revoked" })
                    { Denied(send, mode); count++; }
                    QueuedRetirement(send); count++;
                }
                ContentAndTargets(); count++;
                History(); count++;
                RebuiltAccount(); count++;
                NotificationFlow().GetAwaiter().GetResult(); count++;
                Retention(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
        foreach (string store in new[] { "memory", "file", "sqlite-document" }) foreach (bool reopen in new[] { false, true })
        { Persistence(store, reopen); count++; }
        foreach (string phase in new[] { "insert", "delete" }) foreach (bool reopen in new[] { false, true })
        { StructuredPersistence(phase, reopen); count++; }
        foreach (bool send in new[] { false, true }) foreach (bool authorized in new[] { false, true })
        { HttpIsolation(factory, send, authorized); count++; }
        Console.WriteLine($"PASS: chat module ({count} scenarios)");
    }

    private static void Denied(bool send, string mode)
    {
        var f = new Fixture();
        f.Messages.Add("other", "other-colony", "target", "private", DateTimeOffset.UnixEpoch);
        string user = "user", colony = "colony"; string? token = f.Token;
        if (mode == "missing-user") user = " ";
        if (mode == "missing-colony") colony = "";
        if (mode == "no-token") token = null;
        if (mode == "bad-token") token = "invalid";
        if (mode == "wrong-user") user = "other";
        if (mode == "wrong-colony") colony = "other-colony";
        if (mode == "case") user = "USER";
        if (mode == "expired-token") token = f.S.Tokens.Issue("user", "user", "colony", f.Session, DateTimeOffset.UtcNow.AddDays(-1));
        if (mode is "replaced" or "expired-session")
        {
            f.S.Sessions.EndUser("user");
            f.S.Sessions.TryCreate("user", "colony", mode == "expired-session" ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow, out var next);
            if (mode == "expired-session") token = f.S.Tokens.Issue("user", "user", "colony", next, DateTimeOffset.UtcNow);
        }
        if (mode == "revoked") f.S.Tokens.RevokeForUser("user");
        ProtocolResponse result;
        if (send)
        {
            var response = f.Chat.Send(new(user, colony, token, null, "message")); result = response.Result;
            Require(response.Message is null, "Rejected send exposed a message.");
        }
        else
        {
            var response = f.Chat.List(new(user, colony, token, 17, 200)); result = response.Result;
            Require(response.Messages.Count == 0 && response.LatestSequence == 17, "Rejected history exposed data or changed caller cursor.");
        }
        Require(!result.Accepted && result.ErrorCode == (mode.StartsWith("missing-", StringComparison.Ordinal) ? ProtocolErrorCode.ValidationFailed : ProtocolErrorCode.Unauthorized),
            "Wrong chat rejection: " + mode);
        Require(f.Messages.ListVisible("other", 0, 200).Count == 1 && f.Hub.GetVersion("user") == 0 && f.Hub.GetVersion("target") == 0,
            "Rejected request stored or signalled a message.");
    }

    private static void ContentAndTargets()
    {
        var f = new Fixture();
        foreach (string text in new[] { null!, "", " \r\n\t " })
            Require(f.Send(text).Result.ErrorCode == ProtocolErrorCode.ValidationFailed, "Blank message was accepted.");
        foreach (string target in new[] { "unknown", "TARGET" })
            Require(f.Send("text", target).Result.ErrorCode == ProtocolErrorCode.EventNotFound, "Missing/case-mismatched target was accepted.");
        var before = DateTimeOffset.UtcNow;
        var message = f.Send("  中文 İı العربية 🙂\nhello\r\n ", "  target  ").Message!;
        Require(message.Sequence == 1 && message.MessageId.StartsWith("chat-", StringComparison.Ordinal)
            && message.FromUserId == "user" && message.FromColonyId == "colony" && message.TargetUserId == "target"
            && message.Channel == ChatMessageRegistry.ChannelPrivate && message.Text == "中文 İı العربية 🙂\nhello"
            && message.SentAtUtc >= before && message.SentAtUtc <= DateTimeOffset.UtcNow, "Chat normalization/metadata changed.");
        var publicMessage = f.Send(" " + new string('文', 501) + " ", "\t ").Message!;
        Require(publicMessage.Sequence == 2 && publicMessage.Channel == ChatMessageRegistry.ChannelPublic
            && publicMessage.TargetUserId is null && publicMessage.Text == new string('文', 500), "Public/length normalization changed.");
        long version = f.Hub.GetVersion("user");
        Require(f.Send("self", "user").Result.Accepted && f.Hub.GetVersion("user") == version + 1, "Self-message signalled the same user twice.");
        f.S.Players.Remove("target");
        Require(f.Send("absent", "target").Result.ErrorCode == ProtocolErrorCode.EventNotFound, "Removed player received a new private message.");
        Require(f.Messages.ListVisible("target", 0, 200).Count == 2, "Player removal deleted account chat history.");
    }

    private static void History()
    {
        var f = new Fixture();
        f.Messages.Add("user", "colony", null, "public", DateTimeOffset.UnixEpoch);
        f.Messages.Add("user", "colony", "target", "outgoing", DateTimeOffset.UnixEpoch);
        f.Messages.Add("target", "target-colony", "user", "incoming", DateTimeOffset.UnixEpoch);
        f.Messages.Add("other", "other-colony", "target", "hidden", DateTimeOffset.UnixEpoch);
        f.Messages.Add("other", "other-colony", "USER", "case-hidden", DateTimeOffset.UnixEpoch);
        var latest = f.List(-1, 2);
        Require(latest.Messages.Select(m => m.Sequence).SequenceEqual(new long[] { 2, 3 }) && latest.LatestSequence == 3, "Initial list must return latest visible messages in ascending order.");
        var page = f.List(1, 1);
        Require(page.Messages.Single().Sequence == 2 && page.LatestSequence == 2, "Incremental list skipped the oldest unread visible message.");
        page = f.List(page.LatestSequence, 1);
        Require(page.Messages.Single().Sequence == 3 && page.LatestSequence == 3, "Incremental cursor duplicated or skipped a message.");
        Require(f.List(3).Messages.Count == 0 && f.List(3).LatestSequence == 3 && f.List(long.MaxValue).LatestSequence == long.MaxValue,
            "Hidden messages advanced the cursor or future cursor was reset.");
        Require(f.List(0, 0).Messages.Count == 3 && f.List(0, -1).Messages.Count == 3 && f.Hub.GetVersion("user") == 0,
            "Read changed notification version or default limit.");
    }

    private static void RebuiltAccount()
    {
        var f = new Fixture(); f.Send("old colony", "target");
        f.S.Sessions.EndUser("user"); f.S.Players.Remove("user");
        f.S.Sessions.TryCreate("user", "rebuilt", DateTimeOffset.UtcNow, out var session);
        string token = f.S.Tokens.Issue("user", "user", "rebuilt", session, DateTimeOffset.UtcNow);
        var result = f.Chat.List(new("user", "rebuilt", token, 0, 100));
        Require(result.Result.Accepted && result.Messages.Single().FromColonyId == "colony", "Rebuilt account lost its historical private conversation/source colony.");
        Require(f.List().Result.ErrorCode == ProtocolErrorCode.Unauthorized && f.Send("old session").Result.ErrorCode == ProtocolErrorCode.Unauthorized,
            "Old colony credentials survived account rebuild.");
        Require(f.Chat.Send(new("user", "rebuilt", token, "target", "new colony")).Message?.FromColonyId == "rebuilt", "New colony send used old source identity.");
    }

    private static async Task NotificationFlow()
    {
        var f = new Fixture();
        var streams = new SessionNotifications(f.S.Events, f.Hub, new(), f.S.Notifications);
        var sender = streams.Capture("user", 0, 0); var target = streams.Capture("target", 0, 0); var stranger = streams.Capture("other", 0, 0);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var senderWait = streams.WaitAsync(sender, stop.Token); var targetWait = streams.WaitAsync(target, stop.Token); var otherWait = streams.WaitAsync(stranger, stop.Token);
        f.Send("private", "target");
        var sent = await senderWait.WaitAsync(stop.Token); var received = await targetWait.WaitAsync(stop.Token);
        Require(!otherWait.IsCompleted && f.Hub.GetVersion("other") == 0, "Private send woke an unrelated stream.");
        foreach (var batch in new[] { sent, received })
        {
            var item = batch.Messages.Single();
            Require(item.EventName == "chatChanged" && item.Data is ChatStreamEventDto dto && dto.UserId == batch.Cursor.UserId
                && batch.Cursor.Chat == 1 && batch.Cursor.Ledger == 0, "Chat stream used the wrong event/cursor.");
        }
        Require(f.List().Messages.Single().Text == "private", "Stream wakeup preceded durable message visibility.");
        f.Send("public");
        Require((await otherWait.WaitAsync(stop.Token)).Cursor.Chat == 1 && f.Hub.GetVersion("user") == 2 && f.Hub.GetVersion("target") == 2,
            "Public chat omitted a registered recipient.");
        var next = await streams.WaitAsync(sent.Cursor, stop.Token).WaitAsync(stop.Token);
        Require(next.Cursor.Chat == 2 && next.Messages.Single().EventName == "chatChanged", "Message sent between waits was lost.");
        var resumed = streams.Capture("user", 0, 0);
        Require(resumed.Chat == 2 && f.List().Messages.Count == 2, "Reconnect changed initial cursor/history semantics.");
    }

    private static void QueuedRetirement(bool send)
    {
        var f = new Fixture(); using var started = new ManualResetEventSlim(); Task<ProtocolResponse> pending;
        lock (f.S.Gate)
        {
            pending = Task.Run(() => { started.Set(); return send ? f.Send("queued").Result : f.List().Result; });
            Require(started.Wait(TimeSpan.FromSeconds(2)), "Queued request did not start.");
            f.S.Sessions.EndUser("user"); f.S.Issue();
        }
        Require(pending.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult().ErrorCode == ProtocolErrorCode.Unauthorized
            && f.Messages.ListVisible("user", 0, 100).Count == 0 && f.Hub.GetVersion("user") == 0, "Queued old request escaped session retirement.");
    }

    private static void Retention()
    {
        var f = new Fixture();
        for (int i = 1; i <= 1005; i++) f.Messages.Add("user", "colony", null, "message " + i, DateTimeOffset.UnixEpoch);
        Require(f.List(0, 0).Messages.Count == 100 && f.List(0, int.MaxValue).Messages.Count == 200, "Default/maximum history limit changed.");
        var all = ReadRetained(f.Messages);
        Require(all.Count == 1000 && all[0].Sequence == 6 && all[^1].Sequence == 1005 && all.Select(m => m.Sequence).Distinct().Count() == 1000,
            "Retention or incremental pagination lost/duplicated messages.");
        var fresh = new Fixture();
        Require(fresh.List(-10).LatestSequence == 0 && fresh.List(-10).Messages.Count == 0, "Empty history retained a negative cursor.");
    }

    private static void Persistence(string store, bool reopen) => WithStorage((root, db) =>
    {
        var slot = new RecoveryTestSupport.Slot(store switch
        {
            "file" => new FileJsonPersistenceSlot(Path.Combine(root, "chat.json")),
            "sqlite-document" => new SqliteJsonPersistenceSlot(db, "chat-test"),
            _ => null
        });
        slot.Write(JsonSerializer.Serialize(new { NextSequence = 1011L, Messages = SeedMessages() }));
        var f = new Fixture(new ChatMessageRegistry(slot)); string before = JsonSerializer.Serialize(ReadRetained(f.Messages));
        slot.Before = _ => Require(Monitor.IsEntered(f.S.Gate) && f.Hub.GetVersion("user") == 0, "Chat publication escaped session boundary or notified before save.");
        slot.Fail = true;
        bool failed = false; try { f.Send("retry", "target"); } catch (IOException) { failed = true; }
        Require(failed && before == JsonSerializer.Serialize(ReadRetained(f.Messages)) && before == JsonSerializer.Serialize(ReadRetained(new(slot)))
            && f.Hub.GetVersion("user") == 0 && f.Hub.GetVersion("target") == 0, "Failed save changed history, retention, sequence or notification.");
        slot.Fail = false; slot.Before = null;
        if (reopen) f = new(new ChatMessageRegistry(slot));
        var sent = f.Send("retry", "target").Message!;
        var reopened = ReadRetained(new ChatMessageRegistry(slot));
        Require(sent.Sequence == 1011 && reopened.Count == 1000 && reopened[0].Sequence == 12 && reopened[^1].MessageId == sent.MessageId
            && reopened[^1].TargetUserId == "target" && f.Hub.GetVersion("user") == 1, "Successful retry did not persist exactly one retained message.");
    });

    private static void StructuredPersistence(string phase, bool reopen) => WithStorage((_, db) =>
    {
        IKeyedJsonRecordStore Store() => new SqliteDomainKeyedJsonRecordStore(db, SqliteDomainRegistrySchema.ChatMessages);
        ChatMessageRegistry Open() => new(Store(), null);
        Store().ReplaceAllForImport(SeedMessages().ToDictionary(m => "message:" + m.Sequence.ToString("D20", CultureInfo.InvariantCulture), m => JsonSerializer.Serialize(m)));
        var f = new Fixture(Open()); string before = JsonSerializer.Serialize(ReadRetained(f.Messages));
        void Sql(string sql) { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        Sql($"create trigger fail_chat before {phase} on server_chat_messages begin select raise(abort, 'chat failure'); end;");
        bool failed = false; try { f.Send("durable private", "target"); } catch (SqliteException) { failed = true; }
        Require(failed && before == JsonSerializer.Serialize(ReadRetained(f.Messages)) && before == JsonSerializer.Serialize(ReadRetained(Open()))
            && f.Hub.GetVersion("user") == 0 && f.Hub.GetVersion("target") == 0, "Structured chat batch failed without rollback/cache isolation.");
        Sql("drop trigger fail_chat;"); if (reopen) f = new(Open());
        var sent = f.Send("durable private", "target").Message!;
        var messages = ReadRetained(Open());
        Require(sent.Sequence == 1011 && messages.Count == 1000 && messages[0].Sequence == 12 && messages[^1].MessageId == sent.MessageId,
            "Structured retry lost retention or allocated another sequence.");
        Require(Open().ListVisible("other", 1010, 100).Count == 0 && Open().ListVisible("target", 1010, 100).Single().Text == "durable private",
            "Structured reopen changed private visibility.");
    });

    private static ChatMessageRecord[] SeedMessages() => Enumerable.Range(1, 1000).Select(i =>
        new ChatMessageRecord(i + 10, "seed-" + i, "user", "colony", null, "message " + i, DateTimeOffset.UnixEpoch)).ToArray();

    private static List<ChatMessageRecord> ReadRetained(ChatMessageRegistry registry)
    {
        // These retention fixtures start above sequence 1. A zero cursor selects the latest page.
        var result = new List<ChatMessageRecord>();
        long cursor = 1;
        while (true)
        {
            var page = registry.ListVisible("user", cursor, 200); if (page.Count == 0) return result;
            result.AddRange(page); cursor = page[^1].Sequence;
        }
    }

    private static void WithStorage(Action<string, string> run)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cor-chat-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { string db = Path.Combine(root, "server.sqlite"); ServerDatabaseMigrator.Migrate(db); run(root, db); }
        finally
        {
            SqliteConnection.ClearAllPools();
            Require(Path.GetDirectoryName(root)!.TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                && Path.GetFileName(root).StartsWith("cor-chat-", StringComparison.Ordinal), "Unexpected chat test cleanup path.");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly EventNotificationHub Hub = new();
        internal readonly ChatMessageRegistry Messages;
        internal readonly ChatService Chat;
        internal readonly string Token, Session;
        internal Fixture(ChatMessageRegistry? messages = null)
        {
            Messages = messages ?? new();
            foreach (string user in new[] { "user", "target", "other" }) S.Players.Record(user, user == "user" ? "colony" : user + "-colony", null, DateTimeOffset.UnixEpoch);
            (Session, Token) = S.Issue();
            var authorization = new ColonyAccessAuthorization(S.Tokens, S.Sessions, new(), new(), S.Ledger, () => true);
            Chat = new(authorization, S.Mutations, Messages, S.Players, Hub);
            S.Recovery.OnPrerequisites = S.Recovery.OnCapture = S.Recovery.OnPrepare = S.Recovery.OnReplay = () => throw new Exception("Chat started unrelated snapshot recovery.");
        }
        internal SendChatMessageResponse Send(string text, string? target = null) => Chat.Send(new("user", "colony", Token, target, text));
        internal ListChatMessagesResponse List(long after = 0, int limit = 100) => Chat.List(new("user", "colony", Token, after, limit));
    }

    private static void HttpIsolation(Func<string, SaveSnapshotPackage> factory, bool send, bool authorized)
    {
        using var f = new HttpSnapshotRecoveryTests.Fixture(factory);
        f.Start(); f.Seed(); string before = f.Evidence();
        string token = authorized ? f.Token : "invalid";
        using var response = send
            ? f.Post(ProtocolMessageKind.SendChatMessage, new SendChatMessageRequest("receiver", "away", token, null, "hello"))
            : f.Post(ProtocolMessageKind.ListChatMessages, new ListChatMessagesRequest("receiver", "away", token, 0, 100));
        response.EnsureSuccessStatusCode();
        bool accepted = send
            ? response.Content.ReadFromJsonAsync<SendChatMessageResponse>().GetAwaiter().GetResult()!.Result.Accepted
            : response.Content.ReadFromJsonAsync<ListChatMessagesResponse>().GetAwaiter().GetResult()!.Result.Accepted;
        Require(accepted == authorized, "Chat route changed authorization result.");
        f.AssertPending(before, $"Chat send={send}, authorized={authorized}");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
