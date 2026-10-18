using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Diplomacy;

internal sealed class ClientPlayerDirectory
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientGameRequestTracker requests;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly IClientPlayerDirectoryTransport transport;
    private readonly IClientPlayerSelectionStore selection;
    private readonly IClientPlayerDirectoryPresentation presentation;
    private readonly IClientPlayerDirectoryRuntime runtime;
    private List<ModPlayerSummaryDto> players = new();
    private int version;
    private string? message;

    internal ClientPlayerDirectory(ClientSessionLifecycle session, IClientSnapshotGame game, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, IClientPlayerDirectoryTransport transport, IClientPlayerSelectionStore selection,
        IClientPlayerDirectoryPresentation presentation, IClientPlayerDirectoryRuntime runtime)
    {
        this.session = session; this.synchronization = synchronization; this.mutations = mutations;
        this.transport = transport; this.selection = selection; this.presentation = presentation; this.runtime = runtime;
        requests = new(session, game, () => { players = new(); version++; message = null; });
    }
    internal int Version => requests.Read(() => version);
    internal bool Busy => requests.Busy;
    internal string Message { get => requests.Read(() => message ?? presentation.Text("ClashOfRim.Status.PlayerListNotPulled")); set => requests.Read(() => message = value); }
    internal List<ModPlayerSummaryDto> CopyPlayers() => requests.Read(() => Copy(players));
    internal ClientPlayerSelection Selected => requests.Read(selection.Read);
    internal void Reset() => requests.Reset();
    // Lifecycle callers persist all cleared account settings together.
    internal void ClearSelection() { using var p = ClientSessionWork.EnterPublication(); selection.Write(new("", "", "")); }

    internal Task RefreshAsync(bool manual)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        var context = session.ReadContext();
        var admission = manual ? mutations.CheckAdmission(context.IsConfigured, !string.IsNullOrWhiteSpace(context.CurrentSnapshotId))
            : context.IsConfigured ? LocalMutationAdmission.Allowed : LocalMutationAdmission.NotConfigured;
        if (admission != LocalMutationAdmission.Allowed)
        { requests.TryApply(scope, () => message = presentation.Admission(admission, mutations.Current)); return Task.CompletedTask; }
        ClientSynchronizationCoordinator.Lease? lease = null;
        if (manual && !synchronization.TryBeginManual(out lease))
        { requests.TryApply(scope, () => message = presentation.Text("ClashOfRim.Sync.StatusBusy")); return Task.CompletedTask; }
        var request = requests.Begin(lease, replace: true)!;
        try
        {
            requests.TryApply(request, () => message = presentation.Text("ClashOfRim.Players.StatusRefreshing"));
            return ClientSessionWork.Run(() => ReadAsync(request));
        }
        catch { requests.End(request); throw; }
    }

    private async Task ReadAsync(ClientGameRequestTracker.Request request)
    {
        try
        {
            if (!TryApply(request, static () => { })) return;
            var result = await transport.ListAsync(request.Context, request.Scope.Owner.Token).ConfigureAwait(false);
            string? failure = null;
            List<ModPlayerSummaryDto>? incoming = null;
            if (!TryApply(request, () =>
            {
                if (!result.Success || result.Response is null)
                    failure = presentation.Text("ClashOfRim.Players.StatusRefreshFailed", ("CODE", result.ErrorCode), ("MESSAGE", result.Message));
                else if (result.Response.Result is { Accepted: false } rejected)
                    failure = presentation.Text("ClashOfRim.Players.StatusRefreshRejected", ("CODE", rejected.ErrorCode), ("MESSAGE", rejected.Message));
                else if (result.Response.Result?.Accepted != true || result.Response.Players is null || result.Response.Players.Any(player => player is null))
                    failure = presentation.Text("ClashOfRim.Players.StatusIncomplete");
                else incoming = Copy(result.Response.Players);
            })) return;
            if (incoming is null) { TryApply(request, () => message = failure!); return; }
            await ClientGamePublication.EnqueueAsync(request.Scope.Boundary.Enqueue, request.Scope.Owner.Token,
                action => TryApply(request, action), () => Publish(request, incoming)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryApply(request, () => message = presentation.Text("ClashOfRim.Players.StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message)));
            TryApply(request, () => presentation.Diagnose(ex));
        }
        finally { requests.End(request); }
    }

    private void Publish(ClientGameRequestTracker.Request request, List<ModPlayerSummaryDto> incoming)
    {
        if (!TryApply(request, () => { players = incoming; version++; })) return;
        if (request.Scope.Boundary.HasGame)
            foreach (var player in incoming.Where(player => !string.IsNullOrWhiteSpace(player.UserId) && player.UserId != request.Context.UserId))
            {
                if (!TryApply(request, () => runtime.EnsureProxy(player.UserId))) return;
                if (ClientDiplomacyNames.TryRelation(player.RelationKind, out var relation)
                    && !TryApply(request, () => runtime.ApplyRelation(player.UserId, relation.ToString()))) return;
            }
        var first = incoming.FirstOrDefault(player => player.UserId != request.Context.UserId && Valid(ClientPlayerSelection.From(player)));
        bool empty = false;
        if (!TryApply(request, () => empty = string.IsNullOrWhiteSpace(selection.Read().UserId))) return;
        if (empty && first is not null && !WriteSelection(ClientPlayerSelection.From(first), effect => TryApply(request, effect))) return;
        string summary = "";
        if (!TryApply(request, () => summary = presentation.Summary(Copy(incoming)))) return;
        TryApply(request, () => message = presentation.Text("ClashOfRim.Players.StatusRefreshed", ("PLAYERS", summary)));
    }

    internal bool Select(ClientPlayerSelection target, bool showStatus = true)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        if (!Valid(target)) return false;
        if (target.UserId == scope.Context.UserId && target.ColonyId == scope.Context.ColonyId)
        { requests.TryApply(scope, () => message = presentation.Text("ClashOfRim.Players.StatusCannotSelectSelf")); return false; }
        if (!WriteSelection(target, action => requests.TryApply(scope, action))) return false;
        return requests.TryApply(scope, () =>
        { if (showStatus) message = presentation.Text("ClashOfRim.Players.StatusSelectedTarget", ("USER", target.UserId), ("COLONY", target.ColonyId)); });
    }

    internal void SelectNext()
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        var candidates = players.Select(ClientPlayerSelection.From).Where(target => Valid(target) && target.UserId != scope.Context.UserId).ToList();
        if (candidates.Count == 0) { message = presentation.Text("ClashOfRim.Players.StatusNoSelectableTargets"); return; }
        var current = selection.Read();
        int index = candidates.FindIndex(target => target.UserId == current.UserId && target.ColonyId == current.ColonyId);
        requests.TryApply(scope, () => Select(candidates[(index + 1) % candidates.Count]));
    }

    private bool WriteSelection(ClientPlayerSelection target, Func<Action, bool> tryApply) =>
        tryApply(() => selection.Write(target)) && tryApply(selection.Persist);
    private bool TryApply(ClientGameRequestTracker.Request request, Action action)
    {
        bool applied = false;
        requests.TryApply(request, () => { if (request.Admission?.IsCurrent == false) return; action(); applied = true; });
        return applied;
    }
    private static bool Valid(ClientPlayerSelection target) => !string.IsNullOrWhiteSpace(target.UserId) && !string.IsNullOrWhiteSpace(target.ColonyId);
    private static List<ModPlayerSummaryDto> Copy(List<ModPlayerSummaryDto> source) =>
        ClientProtocolJson.Deserialize<List<ModPlayerSummaryDto>>(ClientProtocolJson.Serialize(source), static type => type);
}
