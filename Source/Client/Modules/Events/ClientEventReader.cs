using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Events;

// One queue -> details pipeline per authenticated identity. Only event reads wait on
// this gate; snapshots and other domains retain their existing admission policies.
internal sealed class ClientEventReader
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientEventInbox inbox;
    private readonly IClientEventReadProtocol protocol;
    private ReadScope? scope;

    internal ClientEventReader(ClientSessionLifecycle session, ClientEventInbox inbox, IClientEventReadProtocol protocol)
    { this.session = session; this.inbox = inbox; this.protocol = protocol; }

    internal event Action<ClientEventReadUpdate>? Updated;

    internal Request Capture(ClientEventReadKind kind, string reason = "", long knownVersion = 0)
    {
        using var publication = ClientSessionWork.EnterPublication();
        // Reuse lifecycle ownership, including identity replacement without a generation change.
        // A replacement identity never waits for the previous identity's HTTP completion.
        if (scope is null || !session.Owns(scope.Owner)) scope = new ReadScope(session.CaptureOwnership());
        return new Request(scope, kind, reason, knownVersion);
    }

    internal bool TryApply(Request request, Action action)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() =>
        {
            if (!session.Owns(request.Scope.Owner)) return;
            action();
            applied = true;
        });
        return applied;
    }

    internal async Task ReadAsync(Request request)
    {
        bool entered = false;
        ClientEventReadStage stage = request.Kind == ClientEventReadKind.Details ? ClientEventReadStage.Details : ClientEventReadStage.Queue;
        try
        {
            await request.Scope.Gate.WaitAsync(request.Scope.Owner.Token).ConfigureAwait(false);
            entered = true;
            // Admission can wait behind a long poll. Capture the latest snapshot only
            // once the read starts, while retaining the originally captured identity.
            ClashOfRimClientNetworkContext? context = null;
            if (!TryApply(request, () => context = session.ReadContext())) return;

            if (request.Kind != ClientEventReadKind.Details)
            {
                if (request.Kind == ClientEventReadKind.Wait)
                {
                    var result = await protocol.WaitAsync(context!, request.KnownVersion, 15, request.Scope.Owner.Token).ConfigureAwait(false);
                    if (!Accept(request, stage, result, result.Response?.Result)) return;
                    TryApply(request, () =>
                    {
                        inbox.ReplaceQueue(result.Response!.EventQueue);
                        Updated?.Invoke(new(request, stage, Queue: result.Response.EventQueue,
                            NotificationVersion: result.Response.NotificationVersion, Changed: result.Response.Changed));
                    });
                    return;
                }

                var queue = await protocol.PullAsync(context!, request.Scope.Owner.Token).ConfigureAwait(false);
                if (!Accept(request, stage, queue, queue.Response?.Result)) return;
                if (!TryApply(request, () =>
                {
                    inbox.ReplaceQueue(queue.Response!.EventQueue);
                    Updated?.Invoke(new(request, stage, Queue: queue.Response.EventQueue));
                })) return;
                if (request.Kind == ClientEventReadKind.Queue) return;
            }

            stage = ClientEventReadStage.Details;
            IReadOnlyList<string>? ids = null;
            if (!TryApply(request, () => ids = inbox.CopyQueuedIds())) return;
            if (ids!.Count == 0)
            {
                TryApply(request, () => Updated?.Invoke(new(request, stage, Empty: true)));
                return;
            }
            var details = await protocol.DetailsAsync(context!, ids, request.Scope.Owner.Token).ConfigureAwait(false);
            if (!Accept(request, stage, details, details.Response?.Result)) return;
            TryApply(request, () =>
            {
                inbox.ReplaceDetails(details.Response!.Events);
                Updated?.Invoke(new(request, stage, Details: inbox.CopyDetails()));
            });
        }
        catch (Exception ex)
        {
            TryApply(request, () => Updated?.Invoke(new(request, stage,
                Failure: new(ClientEventReadFailureKind.Exception, ex.GetType().Name, ex.Message, ex))));
        }
        finally { if (entered) request.Scope.Gate.Release(); }
    }

    private bool Accept<T>(Request request, ClientEventReadStage stage, ClashOfRimClientNetworkResult<T> result, ModProtocolResponseDto? response)
    {
        ClientEventReadFailure? failure = !result.Success || result.Response is null
            ? new(ClientEventReadFailureKind.Transport, result.ErrorCode, result.Message)
            : response is { Accepted: false } ? new(ClientEventReadFailureKind.Rejected, response.ErrorCode.ToString(CultureInfo.InvariantCulture), response.Message) : null;
        if (failure is null) return true;
        TryApply(request, () => Updated?.Invoke(new(request, stage, Failure: failure)));
        return false;
    }

    internal sealed class ReadScope
    {
        internal ReadScope(ClientSessionLifecycle.Ownership owner) => Owner = owner;
        internal ClientSessionLifecycle.Ownership Owner { get; }
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
    }
}

internal enum ClientEventReadKind { Queue, Wait, Details, Refresh, Automatic }
internal enum ClientEventReadStage { Queue, Details }
internal enum ClientEventReadFailureKind { Transport, Rejected, Exception }
internal sealed record ClientEventReadFailure(ClientEventReadFailureKind Kind, string? Code, string? Message, Exception? Exception = null);
internal sealed record ClientEventReadUpdate(ClientEventReader.Request Request, ClientEventReadStage Stage,
    ModEventQueueSummaryDto? Queue = null, IReadOnlyList<ModEventDetailDto>? Details = null,
    long NotificationVersion = 0, bool Changed = false, bool Empty = false, ClientEventReadFailure? Failure = null);
