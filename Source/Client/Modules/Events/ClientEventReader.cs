using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Modules.Events;

// One queue -> details pipeline per authenticated identity. Only event reads wait on
// this gate; snapshots and other domains retain their existing admission policies.
internal sealed class ClientEventReader
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientEventInbox inbox;
    private readonly IClientEventReadProtocol protocol;
    private readonly IClientSnapshotGame game;
    private ReadScope? scope;

    internal ClientEventReader(ClientSessionLifecycle session, ClientEventInbox inbox, IClientEventReadProtocol protocol, IClientSnapshotGame game)
    { this.session = session; this.inbox = inbox; this.protocol = protocol; this.game = game; }

    internal event Action<ClientEventReadUpdate>? Updated;

    internal Request Capture(ClientEventReadKind kind, string reason = "", long knownVersion = 0)
    {
        using var publication = ClientSessionWork.EnterPublication();
        return new Request(CaptureScope(), kind, reason, knownVersion);
    }

    internal ReadScope CaptureScope()
    {
        using var publication = ClientSessionWork.EnterPublication();
        // Identity and game replacement do not wait for an old uncancellable HTTP read.
        if (scope is null || !Owns(scope)) scope = new(session.CaptureOwnership(), session.ReadContext(), game.BindCurrentGame());
        return scope;
    }

    internal bool Owns(ReadScope candidate) => session.Owns(candidate.Owner, candidate.Context, requireSnapshot: false) && candidate.Game.IsCurrent;

    internal bool TryApply(Request request, Action action)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() =>
        {
            if (!Owns(request.Scope) || (request.Context is { } context && !session.Owns(request.Scope.Owner, context, requireSnapshot: true))) return;
            action();
            applied = true;
        });
        return applied;
    }

    internal async Task<ClientEventReadCompletion> ReadAsync(Request request)
    {
        bool entered = false;
        ClientEventReadStage stage = request.Kind == ClientEventReadKind.Details ? ClientEventReadStage.Details : ClientEventReadStage.Queue;
        try
        {
            await request.Scope.Gate.WaitAsync(request.Scope.Owner.Token).ConfigureAwait(false);
            entered = true;
            // Admission can wait behind a long poll. Capture the latest snapshot only
            // once the read starts, while retaining the originally captured identity.
            if (!TryApply(request, () => request.Context = session.ReadContext())) return ClientEventReadCompletion.Obsolete;
            var context = request.Context!;

            if (request.Kind != ClientEventReadKind.Details)
            {
                if (request.Kind == ClientEventReadKind.Wait)
                {
                    var result = await protocol.WaitAsync(context!, request.KnownVersion, 15, request.Scope.Owner.Token).ConfigureAwait(false);
                    if (CheckFailure(request, stage, result, result.Response?.Result) is { } failure) return failure;
                    return TryApply(request, () =>
                    {
                        inbox.ReplaceQueue(result.Response!.EventQueue);
                        Updated?.Invoke(new(request, stage, Queue: result.Response.EventQueue,
                            NotificationVersion: result.Response.NotificationVersion, Changed: result.Response.Changed));
                    }) ? ClientEventReadCompletion.Completed : ClientEventReadCompletion.Obsolete;
                }

                var queue = await protocol.PullAsync(context!, request.Scope.Owner.Token).ConfigureAwait(false);
                if (CheckFailure(request, stage, queue, queue.Response?.Result) is { } queueFailure) return queueFailure;
                if (!TryApply(request, () =>
                {
                    inbox.ReplaceQueue(queue.Response!.EventQueue);
                    Updated?.Invoke(new(request, stage, Queue: queue.Response.EventQueue));
                })) return ClientEventReadCompletion.Obsolete;
                if (request.Kind == ClientEventReadKind.Queue) return ClientEventReadCompletion.Completed;
            }

            stage = ClientEventReadStage.Details;
            IReadOnlyList<string>? ids = null;
            if (!TryApply(request, () => ids = inbox.CopyQueuedIds())) return ClientEventReadCompletion.Obsolete;
            if (ids!.Count == 0)
            {
                return TryApply(request, () => Updated?.Invoke(new(request, stage, Empty: true)))
                    ? ClientEventReadCompletion.Completed : ClientEventReadCompletion.Obsolete;
            }
            var details = await protocol.DetailsAsync(context!, ids, request.Scope.Owner.Token).ConfigureAwait(false);
            if (CheckFailure(request, stage, details, details.Response?.Result) is { } detailsFailure) return detailsFailure;
            return TryApply(request, () =>
            {
                inbox.ReplaceDetails(details.Response!.Events);
                Updated?.Invoke(new(request, stage, Details: inbox.CopyDetails()));
            }) ? ClientEventReadCompletion.Completed : ClientEventReadCompletion.Obsolete;
        }
        catch (Exception ex)
        {
            return TryApply(request, () => Updated?.Invoke(new(request, stage,
                Failure: new(ClientEventReadFailureKind.Exception, ex.GetType().Name, ex.Message, ex))))
                ? ClientEventReadCompletion.Failed : ClientEventReadCompletion.Obsolete;
        }
        finally { if (entered) request.Scope.Gate.Release(); }
    }

    private ClientEventReadCompletion? CheckFailure<T>(Request request, ClientEventReadStage stage, ClashOfRimClientNetworkResult<T> result, ModProtocolResponseDto? response)
    {
        ClientEventReadFailure? failure = !result.Success || result.Response is null
            ? new(ClientEventReadFailureKind.Transport, result.ErrorCode, result.Message)
            : response is { Accepted: false } ? new(ClientEventReadFailureKind.Rejected, response.ErrorCode.ToString(CultureInfo.InvariantCulture), response.Message) : null;
        if (failure is null) return null;
        return TryApply(request, () => Updated?.Invoke(new(request, stage, Failure: failure)))
            ? ClientEventReadCompletion.Failed : ClientEventReadCompletion.Obsolete;
    }

    internal sealed class ReadScope
    {
        internal ReadScope(ClientSessionLifecycle.Ownership owner, ClashOfRimClientNetworkContext context, IClientSnapshotGameSession game)
        { Owner = owner; Context = context; Game = game; }
        internal ClientSessionLifecycle.Ownership Owner { get; }
        internal ClashOfRimClientNetworkContext Context { get; }
        internal IClientSnapshotGameSession Game { get; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);
    }

    internal sealed class Request
    {
        internal Request(ReadScope scope, ClientEventReadKind kind, string reason, long version)
        { Scope = scope; Kind = kind; Reason = reason; KnownVersion = version; }
        internal ReadScope Scope { get; }
        internal ClientEventReadKind Kind { get; }
        internal string Reason { get; }
        internal long KnownVersion { get; }
        internal ClashOfRimClientNetworkContext? Context { get; set; }
    }
}

internal enum ClientEventReadKind { Queue, Wait, Details, Refresh, Automatic }
internal enum ClientEventReadCompletion { Completed, Failed, Obsolete }
internal enum ClientEventReadStage { Queue, Details }
internal enum ClientEventReadFailureKind { Transport, Rejected, Exception }
internal sealed record ClientEventReadFailure(ClientEventReadFailureKind Kind, string? Code, string? Message, Exception? Exception = null);
internal sealed record ClientEventReadUpdate(ClientEventReader.Request Request, ClientEventReadStage Stage,
    ModEventQueueSummaryDto? Queue = null, IReadOnlyList<ModEventDetailDto>? Details = null,
    long NotificationVersion = 0, bool Changed = false, bool Empty = false, ClientEventReadFailure? Failure = null);
