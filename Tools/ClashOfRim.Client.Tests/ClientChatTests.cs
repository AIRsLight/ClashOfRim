using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Sessions;

internal static class ClientChatTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Inputs(); await CacheAndCursor(); await Pagination(); await Coalescing(); await EmptyAndInvalid(); await ConcurrentRequests(); count += 6;
                foreach (bool send in new[] { false, true })
                {
                    foreach (string gate in new[] { "server", "user", "colony", "auth", "expired" }) { await Admission(send, gate); count++; }
                    foreach (string fault in new[] { "network", "missing-response", "missing-result", "rejected", "exception", "canceled", "missing-data", "cursor" })
                    { await Failure(send, fault); count++; }
                    foreach (string phase in new[] { "request", "queue", "success-text" })
                    foreach (string change in new[] { "server", "user", "colony", "auth", "login", "identity", "work", "reset", "expired" })
                    { await Ownership(send, phase, change); count++; }
                    foreach (string change in new[] { "game", "snapshot", "password", "steam" }) { await Unrelated(send, change); count++; }
                    foreach (string phase in new[] { "request", "success-text" }) { await CallbackFailure(send, phase); count++; }
                    await Discard(send); await Replacement(send); await ReentrantCompletion(send); await FrozenReceipt(send); await FrozenFailure(send); count += 5;
                }
            }
            Console.WriteLine($"PASS: chat workflow ({count} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static void Inputs()
    {
        foreach (string? input in new string?[] { null, "", "  \t\n" }) Check(ClientChatInput.Parse(input).Failure?.EndsWith("EmptyMessage") == true, "Empty chat accepted.");
        foreach (string input in new[] { "@", "@bob", "@ bob hi", "@bob   ", "@bob\thi" }) Check(ClientChatInput.Parse(input).Failure?.EndsWith("InvalidPrivateFormat") == true, "Private syntax changed.");
        var plain = ClientChatInput.Parse(" \t消息 İ\nline\n "); var direct = ClientChatInput.Parse(" @玩家İ  私聊 😀  ");
        Check(plain.Target is null && plain.Text == "消息 İ\nline" && direct.Target == "玩家İ" && direct.Text == "私聊 😀", "Culture changed message or recipient.");
        Check(ClientChatInput.Parse(new string('x', 501)).Text.Length == 500 && ClientChatInput.Parse(new string('x', 499) + "😀").Text.Length == 499, "UTF-16 limit damaged a surrogate pair.");
        var f = new Fixture(); f.Module.SendAsync("@broken").GetAwaiter().GetResult();
        Check(f.Requests.Count == 0 && f.Rejections == 1 && !f.Module.Busy, "Input failure sent HTTP or retained busy state.");
    }
    private static async Task CacheAndCursor()
    {
        var f = new Fixture { RepeatQueue = true }; f.Inbox.AddRange(Enumerable.Range(1, 10).Select(i => Message(i, target: i % 2 == 0 ? "alice" : null)));
        await f.Ports.PumpUntil(f.Module.RefreshAsync(initial: true));
        Check(f.Requests.Single().After == 0 && f.Requests.Single().Limit == 20 && f.Module.UnreadPrivate == 5, "Initial page/unread changed.");
        int version = f.Module.Version; var copy = f.Module.CopyMessages(); copy[0].Text = "mutated"; f.Inbox[1].Text = "mutated-source";
        Check(f.Module.CopyMessages().All(m => !m.Text.StartsWith("mutated")), "Chat cache exposed mutable DTOs.");
        f.Module.MarkPrivateRead(); Check(f.Module.UnreadPrivate == 0 && f.Module.Version == version, "Read state rewrote message view.");
        await f.Ports.PumpUntil(f.Module.SendAsync(" @bob hello "));
        Check(f.Requests.Last().Text == "hello" && f.Requests.Last().Target == "bob" && f.Module.CopyMessages().Last().Sequence == 500, "Send normalization/cache failed.");
        f.Inbox.AddRange(Enumerable.Range(11, 25).Select(i => Message(i, target: i == 12 ? "alice" : i == 13 ? "bob" : null)));
        await f.Ports.PumpUntil(f.Module.RefreshAsync());
        Check(f.Requests.Last().After == 10 && f.Module.CopyMessages().Count == 36 && f.Module.UnreadPrivate == 1, "Send receipt skipped unread list messages.");
        version = f.Module.Version; await f.Ports.PumpUntil(f.Module.RefreshAsync(initial: true));
        Check(f.Requests.Last().After == 35 && f.Requests.Last().Limit == 100 && f.Module.Version == version, "Reconnect jumped cursor or duplicate list changed cache.");
        f.Module.Reset(); Check(f.Module.CopyMessages().Count == 0 && f.Module.UnreadPrivate == 0 && f.Module.Version > version && f.Module.Status.Contains("StatusIdle"), "Reset retained conversation state.");
    }
    private static async Task Pagination()
    {
        var f = new Fixture(); f.Inbox.Add(Message(1)); await f.Ports.PumpUntil(f.Module.RefreshAsync(true));
        f.Inbox.AddRange(Enumerable.Range(2, 240).Select(i => Message(i, target: "alice")));
        await f.Ports.PumpUntil(f.Module.RefreshAsync());
        Check(f.Requests.Select(r => r.After).SequenceEqual(new long[] { 0, 1, 101, 201 }), "Full incremental pages were not drained.");
        var snapshot = f.Module.CopyMessages();
        Check(snapshot.Count == 120 && snapshot.First().Sequence == 122 && snapshot.Last().Sequence == 241 && f.Module.UnreadPrivate == 120, "Bounded cache/unread disagreed after trimming.");
        f.Module.MarkPrivateRead(); await f.Ports.PumpUntil(f.Module.RefreshAsync());
        Check(f.Requests.Last().After == 241 && f.Module.UnreadPrivate == 0, "Cursor or read watermark regressed after trimming.");
    }
    private static async Task Coalescing()
    {
        var f = new Fixture(); f.Inbox.Add(Message(1)); await f.Ports.PumpUntil(f.Module.RefreshAsync(true));
        var delayed = new ClientWorldPublicationTestPorts.DelayedReply(); f.Before = _ => delayed.Wait();
        Task work = f.Module.RefreshAsync();
        await delayed.Complete(f.Ports, work, () =>
        {
            for (int i = 0; i < 20; i++) Check(ReferenceEquals(work, f.Module.RefreshAsync(i % 2 == 0)), "Busy refresh did not share its completion.");
            f.Before = null; f.Inbox.Add(Message(2));
        });
        Check(f.Requests.Count == 3 && f.Requests.Last().After == 2 && f.Module.CopyMessages().Count == 2, "Notifications during a request were dropped or amplified.");
    }
    private static async Task EmptyAndInvalid()
    {
        var f = new Fixture(); f.Inbox.Add(Message(1)); await f.Ports.PumpUntil(f.Module.RefreshAsync());
        int version = f.Module.Version;
        f.RawList = new() { Result = new() { Accepted = true }, LatestSequence = 8, Messages = new() { Message(1), Message(1), null!, new() { Sequence = 5 }, Message(8) } };
        await f.Ports.PumpUntil(f.Module.RefreshAsync());
        Check(f.Module.CopyMessages().Count == 2 && f.Module.Version == version + 1, "Duplicate/invalid messages polluted cache.");
        f.RawList = new() { Result = new() { Accepted = true }, LatestSequence = 9, Messages = new() };
        await f.Ports.PumpUntil(f.Module.RefreshAsync()); f.RawList = null; await f.Ports.PumpUntil(f.Module.RefreshAsync());
        Check(f.Requests.Last().After == 9, "Accepted empty page did not acknowledge the server cursor.");
    }
    private static async Task ConcurrentRequests()
    {
        var f = new Fixture(); f.Inbox.Add(Message(1));
        var list = new ClientWorldPublicationTestPorts.DelayedReply(); var send = new ClientWorldPublicationTestPorts.DelayedReply();
        f.Before = sending => (sending ? send : list).Wait();
        Task reading = f.Module.RefreshAsync(); Task writing = f.Module.SendAsync("hello");
        await list.Complete(f.Ports, reading, () => Check(ReferenceEquals(writing, f.Module.SendAsync("another")), "Concurrent send was replayed."));
        Check(f.Module.Busy && f.Module.CopyMessages().Count == 1, "List completion released in-flight send.");
        await send.Complete(f.Ports, writing, () => { });
        Check(!f.Module.Busy && f.Module.CopyMessages().Count == 2 && f.Requests.Count(r => r.Text is not null) == 1, "Parallel receipts overwrote each other.");
        f.Before = null; f.Inbox.Add(Message(2)); await f.Ports.PumpUntil(f.Module.RefreshAsync());
        Check(f.Requests.Last().After == 1 && f.Module.CopyMessages().Count == 3, "Concurrent send advanced list cursor.");
    }
    private static async Task FrozenReceipt(bool send)
    {
        var f = new Fixture(); f.Inbox.Add(Message(1));
        f.Hook = point =>
        {
            if (point != "queue" || f.QueueIndex != (send ? 2 : 1)) return;
            var original = send ? f.SentMessage : f.Inbox[0]; original.Text = "mutated"; original.MessageId = "changed";
        };
        await f.Ports.PumpUntil(f.Start(send));
        var message = f.Module.CopyMessages().Single();
        Check(message.MessageId != "changed" && message.Text != "mutated", "Queued completion retained transport-owned DTOs.");
    }
    private static async Task FrozenFailure(bool send)
    {
        var f = new Fixture { Fault = "rejected" }; f.Inbox.Add(Message(1));
        f.Hook = point =>
        {
            if (point == "queue" && f.QueueIndex == (send ? 2 : 1)) f.LastProtocol!.Message = "changed";
        };
        await f.Ports.PumpUntil(f.Start(send));
        Check(f.Module.Status.Contains("expected") && !f.Module.Status.Contains("changed"), "Queued failure retained mutable transport receipt.");
    }
    private static async Task Admission(bool send, string gate)
    {
        var f = new Fixture(); if (gate == "expired") f.Change(gate); else f.Clear(gate);
        await f.Ports.PumpUntil(f.Start(send));
        Check(f.Requests.Count == 0 && !f.Module.Busy && f.Module.Status.Contains("StatusNotConnected"), "Disconnected chat reached HTTP.");
    }
    private static async Task Failure(bool send, string fault)
    {
        var f = new Fixture { Fault = fault }; f.Inbox.Add(Message(1));
        await f.Ports.PumpUntil(f.Start(send));
        bool accepted = send && fault == "cursor";
        Check(!f.Module.Busy && f.Requests.Count == 1 && f.Module.CopyMessages().Count == (accepted ? 1 : 0), "Failure continued, duplicated send or consumed data: " + send + "/" + fault);
        Check(accepted || f.Module.Status.Contains("Failed") || f.Module.Status.Contains("Rejected") || f.Module.Status.Contains("Exception"), "Failure not presented.");
        f.Fault = null; await f.Ports.PumpUntil(f.Start(send));
        Check(!f.Module.Busy && f.Module.CopyMessages().Count == 1 && (send || f.Requests.Last().After == 0), "Failure blocked retry or consumed cursor.");
    }
    private static async Task Ownership(bool send, string phase, string change)
    {
        var f = new Fixture(); f.Inbox.Add(Message(1)); bool changed = false; int count = 0;
        f.Hook = point => { if (point == phase && !changed) { changed = true; count = f.Requests.Count; f.Change(change); } };
        await f.Ports.PumpUntil(f.Start(send));
        Check(changed && f.Requests.Count == count && f.Module.CopyMessages().Count == 0 && !f.Module.Busy,
            "Old chat continued across " + send + "/" + phase + "/" + change);
    }
    private static async Task Unrelated(bool send, string change)
    {
        var f = new Fixture(); f.Inbox.Add(Message(1)); f.Hook = phase => { if (phase == "request") f.Change(change); };
        await f.Ports.PumpUntil(f.Start(send));
        Check(f.Module.CopyMessages().Count == 1 && !f.Module.Busy, "Account conversation bound to unrelated resource: " + change);
    }
    private static async Task CallbackFailure(bool send, string phase)
    {
        var f = new Fixture(); f.Inbox.Add(Message(1)); bool threw = false;
        f.Hook = point => { if (phase == point && !threw) { threw = true; throw new InvalidOperationException("expected"); } };
        await f.Ports.PumpUntil(f.Start(send), ex => { if (ex is not InvalidOperationException || ex.Message != "expected") throw ex; });
        Check(threw && !f.Module.Busy && f.Diagnostics == 1 && f.Module.CopyMessages().Count == 0, "Callback failure leaked work or published partial cache.");
    }
    private static async Task Discard(bool send)
    {
        var f = new Fixture { DiscardIndex = send ? 2 : 1, RepeatQueue = true }; f.Inbox.Add(Message(1));
        await f.Ports.PumpUntil(f.Start(send));
        Check(!f.Module.Busy && f.Module.CopyMessages().Count == 0 && f.Requests.Count == 1, "Discarded callback replayed/resent chat.");
    }
    private static async Task Replacement(bool send)
    {
        var f = new Fixture(); f.Inbox.Add(Message(1)); var first = new ClientWorldPublicationTestPorts.DelayedReply(); var second = new ClientWorldPublicationTestPorts.DelayedReply(); int calls = 0;
        f.Before = _ => (++calls == 1 ? first : second).Wait(); Task? successor = null;
        Task old = f.Start(send);
        await first.Complete(f.Ports, old, () => { f.Change("login"); successor = f.Start(send); });
        Check(f.Module.Busy && f.Module.CopyMessages().Count == 0, "Old finalizer cleared successor or published across login.");
        await second.Complete(f.Ports, successor!, () => { });
        Check(!f.Module.Busy && f.Module.CopyMessages().Count == 1 && f.Requests.Last().Context.AuthToken == "replacement", "Replacement lost captured credentials.");
    }
    private static async Task ReentrantCompletion(bool send)
    {
        var f = new Fixture(); f.Inbox.Add(Message(1)); bool changed = false; Task? next = null;
        f.Hook = point => { if (point == "success-text" && !changed) { changed = true; f.Module.Reset(); next = f.Start(send); } };
        await f.Ports.PumpUntil(f.Start(send)); await f.Ports.PumpUntil(next!);
        Check(f.Module.CopyMessages().Count == 1 && f.Requests.Count == 2 && !f.Module.Busy, "Reentrant cache reset adopted old result or lost new work.");
    }
    private static ModChatMessageDto Message(long sequence, string? target = null) => new()
    { Sequence = sequence, MessageId = "message-" + sequence.ToString(CultureInfo.InvariantCulture), Channel = target is null ? "Public" : "Private", FromUserId = "bob", TargetUserId = target, Text = "消息 İ " + sequence };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IClientChatTransport, IClientChatPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientChat Module;
        internal readonly List<ModChatMessageDto> Inbox = new();
        internal readonly ConcurrentQueue<(ClashOfRimClientNetworkContext Context, long After, int Limit, string? Target, string? Text)> Requests = new();
        internal ModChatMessageDto SentMessage = Message(500);
        internal string? Fault; internal bool RepeatQueue; internal int Rejections, Diagnostics, DiscardIndex, QueueIndex;
        internal ModListChatMessagesResponseDto? RawList;
        internal ModProtocolResponseDto? LastProtocol;
        internal Action<string>? Hook; internal Func<bool, Task>? Before;
        internal Fixture()
        { ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports)); Session.PublishLogin(new() { AuthToken = "auth", SessionId = "original" }); Module = new(Session, this, this); }
        internal Task Start(bool send) => send ? Module.SendAsync("hello") : Module.RefreshAsync();
        internal void Clear(string field)
        { switch (field) { case "server": Ports.Server = ""; break; case "user": Ports.User = ""; break; case "colony": Ports.Colony = ""; break; case "auth": Ports.Auth = ""; break; } }
        internal void Change(string field)
        {
            switch (field)
            {
                case "server": Ports.Server = "http://other"; break; case "user": Ports.User = "other"; break;
                case "colony": Ports.Colony = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "login": Session.PublishLogin(new() { AuthToken = "replacement", SessionId = "new" }); break;
                case "identity": Session.PublishLogin(new() { AuthToken = Ports.Auth, SessionId = "new" }); break;
                case "work": ClientSessionWork.Reset(); break; case "reset": Module.Reset(); break;
                case "expired": Session.BeginUnavailable(Ports.Auth, null); break; case "game": Ports.GameOwner = new(); break;
                case "snapshot": Ports.Snapshot = "advanced"; break; case "password": Ports.Password = "new"; break; case "steam": Ports.SteamTicket = "new"; break;
            }
        }
        private async Task Network(bool send) { Hook?.Invoke("request"); if (Before is not null) await Before(send); }
        public async Task<ClashOfRimClientNetworkResult<ModSendChatMessageResponseDto>> SendAsync(ClashOfRimClientNetworkContext context, string? target, string text, CancellationToken token)
        {
            Requests.Enqueue((context, 0, 0, target, text)); await Network(true);
            SentMessage = Message(500, target);
            return Result(new ModSendChatMessageResponseDto { Result = Protocol(), Message = Fault == "missing-data" ? null : SentMessage });
        }
        public async Task<ClashOfRimClientNetworkResult<ModListChatMessagesResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, long after, int limit, CancellationToken token)
        {
            Requests.Enqueue((context, after, limit, null, null)); await Network(false);
            var messages = after == 0 ? Inbox.TakeLast(limit).ToList() : Inbox.Where(m => m.Sequence > after).Take(limit).ToList();
            return Result(RawList ?? new ModListChatMessagesResponseDto { Result = Protocol(), Messages = Fault == "missing-data" ? null! : messages,
                LatestSequence = Fault == "cursor" ? -1 : messages.LastOrDefault()?.Sequence ?? after });
        }
        private ModProtocolResponseDto? Protocol() => LastProtocol = Fault == "missing-result" ? null : new() { Accepted = Fault != "rejected", ErrorCode = 17, Message = "expected" };
        private ClashOfRimClientNetworkResult<T> Result<T>(T response) => Fault switch
        { "network" => ClashOfRimClientNetworkResult<T>.Failed("network", "expected"), "missing-response" => ClashOfRimClientNetworkResult<T>.Ok(default!),
            "exception" => throw new InvalidOperationException("expected"), "canceled" => throw new TaskCanceledException("expected"), _ => ClashOfRimClientNetworkResult<T>.Ok(response) };
        public string Text(string key, params (string Name, object? Value)[] args)
        {
            if (key.EndsWith("StatusRefreshed", StringComparison.Ordinal) || key.Contains("StatusSent")) Hook?.Invoke("success-text");
            return key + " " + string.Join(';', args.Select(a => a.Value));
        }
        public void Enqueue(Action action, Action discard)
        {
            int index = ++QueueIndex; Hook?.Invoke("queue");
            Ports.Enqueue(() => { if (index == DiscardIndex) { discard(); if (RepeatQueue) action(); } else { action(); if (RepeatQueue) action(); } });
        }
        public void Reject(string text) => Rejections++;
        public void Diagnose(Exception error) => Diagnostics++;
    }
}
