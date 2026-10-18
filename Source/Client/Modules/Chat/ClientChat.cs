using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Chat;

internal interface IClientChatPresentation
{
    string Text(string key, params (string Name, object? Value)[] args);
    void Enqueue(Action action, Action discard);
    void Reject(string text);
    void Diagnose(Exception error);
}

// Account/session-owned conversation state. Game and snapshot changes do not
// invalidate chat. Send receipts enter the cache but never advance the read cursor.
internal sealed class ClientChat
{
    private const int RetainedMessages = 120, InitialPage = 20, IncrementalPage = 100;
    private readonly ClientSessionLifecycle session;
    private readonly ClientRequestTracker state;
    private readonly IClientChatTransport transport;
    private readonly IClientChatPresentation view;
    private readonly List<ModChatMessageDto> messages = new();
    private readonly Job?[] active = new Job?[2];
    private long cursor, readPrivate;
    private int unread, version;
    private string status = "";
    internal ClientChat(ClientSessionLifecycle session, IClientChatTransport transport, IClientChatPresentation view)
    {
        this.session = session; this.transport = transport; this.view = view;
        state = new(session, () => { messages.Clear(); cursor = readPrivate = 0; unread = 0; status = ""; version++; Array.Clear(active, 0, active.Length); });
    }
    internal bool Busy => state.Read(() => active.Any(job => job is not null));
    internal int Version => state.Read(() => version);
    internal int UnreadPrivate => state.Read(() => unread);
    internal IReadOnlyList<ModChatMessageDto> CopyMessages() => state.Read(() => ClientProtocolJson.Copy(messages));
    internal string Status
    {
        get
        {
            using var p = ClientSessionWork.EnterPublication();
            var scope = state.Capture();
            if (status.Length == 0) SetStatus(scope, "ClashOfRim.Chat.StatusIdle");
            return status;
        }
    }
    internal void Reset() => state.Reset();
    internal void MarkPrivateRead()
    {
        state.Read(() =>
        {
            readPrivate = Math.Max(readPrivate, messages.Where(IsIncomingPrivate).Select(message => message.Sequence).DefaultIfEmpty(readPrivate).Max());
            unread = 0; return true;
        });
    }
    private bool IsIncomingPrivate(ModChatMessageDto message) => message.Channel == "Private" && message.TargetUserId == session.ReadContext().UserId;
    internal Task RefreshAsync(bool initial = false) => Start(null, initial);
    internal Task SendAsync(string? text) => Start(ClientChatInput.Parse(text), false);
    private Task Start(ClientChatInput? input, bool initial)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = state.Capture(); var context = session.ReadContext();
        if (session.IsUnavailable || !context.IsConfigured || string.IsNullOrWhiteSpace(context.AuthToken))
        { SetStatus(scope, "ClashOfRim.Chat.StatusNotConnected"); return Task.CompletedTask; }
        int slot = input is null ? 0 : 1;
        if (active[slot] is { } pending) { if (slot == 0) pending.Repeat = true; return pending.Work; }
        if (input?.Failure is { } failure)
        {
            SetStatus(scope, failure);
            state.TryApply(scope, () => view.Reject(status)); return Task.CompletedTask;
        }
        var job = new Job(scope, context, input, initial); active[slot] = job;
        return job.Work = ClientSessionWork.Run(() => Run(job));
    }
    private bool Apply(Job job, Action action)
    {
        bool applied = false;
        state.TryApply(job.Scope, () =>
        {
            if (session.IsUnavailable || !ReferenceEquals(active[job.Slot], job)) return;
            action(); applied = true;
        });
        return applied;
    }
    private bool Current(Job job) => Apply(job, static () => { });
    private async Task<bool> Publish(Job job, Action action)
    {
        bool consumed = false, applied = false;
        await ClientGamePublication.EnqueueAsync((run, discard) => view.Enqueue(run, () => { consumed = true; discard(); }),
            job.Scope.Owner.Token, callback => Apply(job, callback), () =>
            { if (consumed) return; consumed = true; action(); applied = true; }).ConfigureAwait(false);
        return applied && Current(job);
    }
    private async Task Run(Job job)
    {
        try
        {
            if (job.Input is not null && !await Publish(job, () => SetStatus(job, "ClashOfRim.Chat.StatusSending")).ConfigureAwait(false)) return;
            while (Current(job))
            {
                long after = 0; int limit = 0;
                if (!Apply(job, () => { job.Repeat = false; after = cursor; limit = job.Initial && after == 0 ? InitialPage : IncrementalPage; job.Initial = false; })) return;
                var reply = await Request(job, after, limit).ConfigureAwait(false);
                if (!Current(job)) return;
                reply = reply with { Result = reply.Result is null ? null : ClientProtocolJson.Copy(reply.Result) };
                string operation = job.Input is null ? "Refresh" : "Send";
                if (!reply.Success || reply.Messages is null || reply.Result?.Accepted != true)
                {
                    bool denied = reply.Success && reply.Result is { Accepted: false };
                    await Publish(job, () => SetStatus(job, "ClashOfRim.Chat.Status" + operation + (denied ? "Rejected" : "Failed"),
                        ("CODE", denied ? reply.Result!.ErrorCode : reply.Success ? "InvalidResponse" : reply.Error),
                        ("MESSAGE", denied ? reply.Result!.Message : reply.ErrorMessage))).ConfigureAwait(false);
                    return;
                }
                var incoming = ClientProtocolJson.Copy(reply.Messages);
                bool more = false;
                if (!await Publish(job, () =>
                {
                    string key = job.Input is null ? "StatusRefreshed" : job.Input.Target is null ? "StatusSentPublic" : "StatusSentPrivate";
                    string text = view.Text("ClashOfRim.Chat." + key, ("COUNT", incoming.Count), ("TARGET", job.Input?.Target));
                    if (!Current(job)) return;
                    Merge(incoming);
                    // Only accepted list pages acknowledge all visible messages up
                    // to LatestSequence. An out-of-order send cannot skip a gap.
                    if (job.Input is null)
                    {
                        cursor = Math.Max(cursor, reply.Latest);
                        more = after > 0 && incoming.Count >= limit && cursor > after;
                    }
                    status = text;
                }).ConfigureAwait(false)) return;
                bool again = false;
                Apply(job, () =>
                {
                    again = job.Input is null && (more || job.Repeat);
                    if (!again) active[job.Slot] = null;
                });
                if (!again) return;
            }
        }
        catch (Exception ex)
        {
            if (Current(job)) await Publish(job, () =>
            {
                SetStatus(job, "ClashOfRim.Chat.StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
                Apply(job, () => view.Diagnose(ex));
            }).ConfigureAwait(false);
        }
        finally { state.TryApply(job.Scope, () => { if (ReferenceEquals(active[job.Slot], job)) active[job.Slot] = null; }); }
    }
    private async Task<Reply> Request(Job job, long after, int limit)
    {
        if (job.Input is { } input)
        {
            var response = await transport.SendAsync(job.Context, input.Target, input.Text, job.Scope.Owner.Token).ConfigureAwait(false);
            return new(response.Success, response.Response?.Result, response.Response?.Message is { } message ? new() { message } : null, 0, response.ErrorCode, response.Message);
        }
        var read = await transport.ListAsync(job.Context, after, limit, job.Scope.Owner.Token).ConfigureAwait(false);
        var messages = read.Response?.Messages;
        long latest = read.Response?.LatestSequence ?? after;
        // A malformed page must not consume the cursor or start a paging loop.
        if (latest < after || messages?.Any(message => message is not null && message.Sequence > latest) == true) messages = null;
        return new(read.Success, read.Response?.Result, messages, latest, read.ErrorCode, read.Message);
    }
    private void Merge(IEnumerable<ModChatMessageDto> incoming)
    {
        var ids = new HashSet<string>(messages.Select(message => message.MessageId), StringComparer.Ordinal);
        bool changed = false;
        foreach (var message in incoming)
            if (message is not null && !string.IsNullOrWhiteSpace(message.MessageId) && message.Sequence > 0 && ids.Add(message.MessageId))
            { messages.Add(message); changed = true; }
        if (!changed) return;
        messages.Sort((a, b) => { int sequence = a.Sequence.CompareTo(b.Sequence); return sequence != 0 ? sequence : StringComparer.Ordinal.Compare(a.MessageId, b.MessageId); });
        if (messages.Count > RetainedMessages) messages.RemoveRange(0, messages.Count - RetainedMessages);
        unread = messages.Count(message => IsIncomingPrivate(message) && message.Sequence > readPrivate); version++;
    }
    private void SetStatus(ClientRequestTracker.Scope scope, string key, params (string Name, object? Value)[] args)
        => SetStatus(action => state.TryApply(scope, action), key, args);
    private void SetStatus(Job job, string key, params (string Name, object? Value)[] args)
        => SetStatus(action => Apply(job, action), key, args);
    private void SetStatus(Func<Action, bool> apply, string key, params (string Name, object? Value)[] args)
    {
        string text = "";
        if (apply(() => text = view.Text(key, args))) apply(() => status = text);
    }
    private sealed record Reply(bool Success, ModProtocolResponseDto? Result, List<ModChatMessageDto>? Messages, long Latest, string? Error, string? ErrorMessage);
    private sealed class Job
    {
        internal readonly ClientRequestTracker.Scope Scope;
        internal readonly ClashOfRimClientNetworkContext Context;
        internal readonly ClientChatInput? Input;
        internal int Slot => Input is null ? 0 : 1;
        internal bool Initial, Repeat;
        internal Task Work = Task.CompletedTask;
        internal Job(ClientRequestTracker.Scope scope, ClashOfRimClientNetworkContext context, ClientChatInput? input, bool initial)
        { Scope = scope; Context = context; Input = input; Initial = initial; }
    }
}
