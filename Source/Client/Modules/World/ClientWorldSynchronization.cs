using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal enum ClientWorldSynchronizationKind { Markers, CaravanTargets, RuntimeObjects }
internal interface IClientWorldSynchronizationRuntime { IClientWorldSynchronizationSession Bind(); }
internal interface IClientWorldSynchronizationSession
{
    bool IsCurrent { get; }
    bool HasWorldObjects { get; }
    bool HasRemoteSession { get; }
    int Tick { get; }
    IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureObjects();
    IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureCaravans();
    void Enqueue(Action action, Action discard);
}
internal interface IClientWorldSynchronizationPresentation : IClientWorldPreparationPresentation { void Status(string text); }

// Reads, caravan arrival refreshes and runtime uploads share admission/publication.
// Polling remembers accepted batches, so failures cannot consume change detection.
internal sealed class ClientWorldSynchronization
{
    private const int PollTicks = 250;
    private const int RuntimeLeaseTicks = 2500;
    private sealed record Boundary(IClientWorldSynchronizationSession Runtime, string World);
    private sealed record Input(ClientWorldSynchronizationKind Kind, string Reason, string Signature,
        ModRuntimeWorldObjectMarkerDto[] Objects, Func<Task>? RefreshTrades, bool Polled);
    private readonly ClientRequestTracker<Boundary> requests;
    private readonly ClientSessionLifecycle session;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientWorldMarkers markers;
    private readonly IClientWorldMarkerTransport transport;
    private readonly IClientWorldSynchronizationPresentation view;
    private readonly Func<Func<Task>> captureTrades;
    private readonly Input?[] pending = new Input?[3];
    private Input? active;
    private string acceptedObjects = "", acceptedCaravans = "";
    private int nextCheck, nextLease;
    private readonly ClientWorldMarkerRefreshSchedule viewRefresh = new();
    internal ClientWorldSynchronization(ClientSessionLifecycle session, IClientWorldIdentity identity, ClientLocalMutationCoordinator mutations,
        ClientWorldMarkers markers, IClientWorldSynchronizationRuntime runtime, IClientWorldMarkerTransport transport,
        IClientWorldSynchronizationPresentation view, Func<Func<Task>> captureTrades)
    {
        this.session = session; this.mutations = mutations; this.markers = markers; this.transport = transport; this.view = view; this.captureTrades = captureTrades;
        requests = new(session, () => new(runtime.Bind(), identity.WorldId), b => b.Runtime.IsCurrent && b.World == identity.WorldId, Clear);
    }
    private void Clear() { Array.Clear(pending, 0, pending.Length); active = null; acceptedObjects = acceptedCaravans = ""; nextCheck = nextLease = 0; viewRefresh.Reset(); }
    internal void Reset() => requests.Reset();
    internal bool Busy => requests.Busy;
    internal bool CanStart(ClientWorldSynchronizationKind kind) => requests.Read(() => !requests.Busy && Eligible(kind, requests.Capture()));
    internal Task RefreshViewAsync(bool selected, float now)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        var refresh = viewRefresh.Advance(selected, now);
        if (refresh is null || session.IsUnavailable) return Task.CompletedTask;
        string reason = view.Text(refresh == ClientWorldMarkerRefresh.Entered
            ? "ClashOfRim.WorldMap.ReasonEnteredWorldMap" : "ClashOfRim.WorldMap.ReasonStayedWorldMap");
        return requests.TryApply(scope, static () => { }) ? RequestAsync(ClientWorldSynchronizationKind.Markers, reason) : Task.CompletedTask;
    }
    private bool Eligible(ClientWorldSynchronizationKind kind, ClientRequestTracker<Boundary>.Scope scope)
    {
        var context = session.ReadContext(); var runtime = scope.Boundary.Runtime;
        if (session.IsUnavailable || !context.IsConfigured || !runtime.HasWorldObjects) return false;
        if (kind == ClientWorldSynchronizationKind.Markers) return true;
        return !runtime.HasRemoteSession && !string.IsNullOrWhiteSpace(session.SessionId)
            && (kind == ClientWorldSynchronizationKind.CaravanTargets ? !string.IsNullOrWhiteSpace(context.CurrentSnapshotId) : !mutations.BlocksGameLifecycle);
    }
    internal Task RequestAsync(ClientWorldSynchronizationKind kind, string reason, string? caravanSignature = null)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        try
        {
            if (!Eligible(kind, scope))
            {
                if (kind == ClientWorldSynchronizationKind.Markers && !session.IsUnavailable)
                    Notice(scope, !session.ReadContext().IsConfigured ? "StatusNotConfigured" : "StatusWorldObjectsMissing");
                return Task.CompletedTask;
            }
            if (kind == ClientWorldSynchronizationKind.CaravanTargets && string.IsNullOrWhiteSpace(caravanSignature)) return Task.CompletedTask;
            var objects = kind == ClientWorldSynchronizationKind.RuntimeObjects ? Copy(scope.Boundary.Runtime.CaptureObjects()) : Array.Empty<ModRuntimeWorldObjectMarkerDto>();
            var input = new Input(kind, reason, kind == ClientWorldSynchronizationKind.RuntimeObjects ? Signature(objects) : caravanSignature ?? "",
                objects, kind == ClientWorldSynchronizationKind.CaravanTargets ? captureTrades() : null, false);
            return Queue(scope, input);
        }
        catch (Exception ex) { Failure(scope, kind, ex); return Task.CompletedTask; }
    }
    internal Task Poll(int ticks)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        if (ticks < nextCheck) return Task.CompletedTask; nextCheck = ticks + PollTicks;
        Task work = Task.CompletedTask;
        foreach (var kind in new[] { ClientWorldSynchronizationKind.RuntimeObjects, ClientWorldSynchronizationKind.CaravanTargets })
        {
            if (!requests.TryApply(scope, static () => { })) return work;
            try
            {
                if (!Eligible(kind, scope)) continue;
                var data = Copy(kind == ClientWorldSynchronizationKind.RuntimeObjects ? scope.Boundary.Runtime.CaptureObjects() : scope.Boundary.Runtime.CaptureCaravans());
                if (!requests.TryApply(scope, static () => { }) || !Eligible(kind, scope)) return work;
                string signature = Signature(data);
                if (active?.Kind == kind && active.Signature == signature) { ForgetPolled(kind); continue; }
                if (kind == ClientWorldSynchronizationKind.CaravanTargets)
                {
                    if (data.Length == 0) { acceptedCaravans = ""; ForgetPolled(kind); continue; }
                    if (active?.Kind != kind && signature == acceptedCaravans) { ForgetPolled(kind); continue; }
                }
                else if (active?.Kind != kind && signature == acceptedObjects && (data.Length == 0 || ticks < nextLease)) { ForgetPolled(kind); continue; }
                work = Task.WhenAll(work, Queue(scope, new(kind, kind == ClientWorldSynchronizationKind.RuntimeObjects ? "runtime-world-object-sync" : "caravan-target-refresh",
                    signature, kind == ClientWorldSynchronizationKind.RuntimeObjects ? data : Array.Empty<ModRuntimeWorldObjectMarkerDto>(),
                    kind == ClientWorldSynchronizationKind.CaravanTargets ? captureTrades() : null, true)));
            }
            catch (Exception ex) { Failure(scope, kind, ex); }
        }
        return work;
    }
    private void ForgetPolled(ClientWorldSynchronizationKind kind)
    { if (pending[(int)kind]?.Polled == true) pending[(int)kind] = null; }
    private Task Queue(ClientRequestTracker<Boundary>.Scope scope, Input input)
    {
        if (!requests.TryApply(scope, static () => { }) || !Eligible(input.Kind, scope) || !requests.TryApply(scope, static () => { })) return Task.CompletedTask;
        if (requests.Busy)
        {
            if (input.Polled && active?.Kind == input.Kind && active.Signature == input.Signature) { ForgetPolled(input.Kind); return Task.CompletedTask; }
            pending[(int)input.Kind] = input; return Task.CompletedTask;
        }
        pending[(int)input.Kind] = null;
        var observation = markers.CaptureObservation();
        if (!requests.TryApply(scope, static () => { })) return Task.CompletedTask;
        var request = requests.Begin()!; active = input;
        return ExecuteAsync(request, input, observation);
    }
    private async Task ExecuteAsync(ClientRequestTracker<Boundary>.Request request, Input input, ClientWorldMarkers.Observation observation)
    {
        var scope = request.Scope; var runtime = scope.Boundary.Runtime;
        bool Current() => !session.IsUnavailable && requests.TryApply(request, static () => { });
        bool Ready() => Current() && Eligible(input.Kind, scope) && Current();
        Task Publish(Action action)
        {
            bool consumed = false;
            return ClientGamePublication.EnqueueAsync((run, discard) => runtime.Enqueue(run, () => { consumed = true; discard(); }), scope.Owner.Token,
                run => requests.TryApply(request, run), () => { if (consumed || !Ready()) return; consumed = true; action(); });
        }
        try
        {
            if (!Ready()) return;
            if (input.Kind != ClientWorldSynchronizationKind.RuntimeObjects)
                Notice(scope, input.Kind == ClientWorldSynchronizationKind.Markers ? "StatusSyncing" : "StatusCaravanTargetsRefreshing");
            await ClientSessionWork.Run(async () =>
            {
                if (!Current()) return;
                ModWorldMapMarkerDeliveryDto? delivery;
                string? code, message; bool success;
                if (input.Kind == ClientWorldSynchronizationKind.RuntimeObjects)
                {
                    var result = await transport.WriteAsync(request.Context, input.Objects, scope.Owner.Token).ConfigureAwait(false);
                    success = result.Success && result.Response is not null && result.Response.Result?.Accepted != false;
                    code = result.Response?.Result?.Accepted == false ? result.Response.Result.ErrorCode.ToString(CultureInfo.InvariantCulture) : result.ErrorCode;
                    message = result.Response?.Result?.Accepted == false ? result.Response.Result.Message : result.Message;
                    delivery = result.Response?.WorldMapMarkers;
                }
                else
                {
                    var result = await transport.ReadAsync(request.Context, scope.Owner.Token).ConfigureAwait(false);
                    success = result.Success && result.Response is not null; code = result.ErrorCode; message = result.Message; delivery = result.Response;
                }
                if (!Current()) return;
                var frozen = delivery is null ? null : ClientProtocolJson.Copy(delivery);
                if (!success)
                {
                    await Publish(() =>
                    {
                        if (input.Kind != ClientWorldSynchronizationKind.RuntimeObjects)
                            Notice(scope, input.Kind == ClientWorldSynchronizationKind.Markers ? "StatusSyncFailed" : "StatusCaravanTargetsFailed", ("CODE", code ?? ""), ("MESSAGE", message ?? ""));
                        if (Current()) view.Diagnose("World " + input.Kind + " sync failed: " + code + " " + message);
                    }).ConfigureAwait(false);
                    return;
                }
                Task followup = Task.CompletedTask; bool published = false;
                await Publish(() =>
                {
                    markers.Observe(observation, frozen, immediately: true, reason: input.Reason, current: Ready);
                    if (!Ready()) return;
                    if (input.Kind == ClientWorldSynchronizationKind.RuntimeObjects)
                    { int tick = runtime.Tick; if (!Ready()) return; acceptedObjects = input.Signature; nextLease = tick + RuntimeLeaseTicks; }
                    else if (input.Kind == ClientWorldSynchronizationKind.CaravanTargets)
                        followup = input.RefreshTrades!();
                    published = true;
                }).ConfigureAwait(false);
                if (!published) return;
                await followup.ConfigureAwait(false);
                if (input.Kind == ClientWorldSynchronizationKind.CaravanTargets)
                    await Publish(() => acceptedCaravans = input.Signature).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { if (Current()) await Publish(() => Failure(scope, input.Kind, ex)).ConfigureAwait(false); }
        finally
        {
            bool owned = requests.TryApply(request, () => active = null); requests.End(request);
            if (owned && pending.Any(value => value is not null))
            {
                Task next = Task.CompletedTask; bool consumed = false;
                await ClientGamePublication.EnqueueAsync(runtime.Enqueue, scope.Owner.Token, action => requests.TryApply(scope, action), () =>
                {
                    if (consumed) return; consumed = true;
                    foreach (var value in pending)
                        if (value is not null && Eligible(value.Kind, scope)) { next = Queue(scope, value); break; }
                }).ConfigureAwait(false);
                await next.ConfigureAwait(false);
            }
        }
    }
    private void Notice(ClientRequestTracker<Boundary>.Scope scope, string key, params (string Name, object? Value)[] args)
    {
        string text = "";
        if (!requests.TryApply(scope, () => text = view.Text("ClashOfRim.WorldMap." + key, args)) || session.IsUnavailable) return;
        requests.TryApply(scope, () => view.Status(text));
    }
    private void Failure(ClientRequestTracker<Boundary>.Scope scope, ClientWorldSynchronizationKind kind, Exception ex)
    {
        if (!requests.TryApply(scope, static () => { })) return;
        if (kind != ClientWorldSynchronizationKind.RuntimeObjects)
            Notice(scope, kind == ClientWorldSynchronizationKind.Markers ? "StatusSyncException" : "StatusCaravanTargetsException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
        requests.TryApply(scope, () => view.Diagnose("World " + kind + " sync exception: " + ex));
    }
    private static ModRuntimeWorldObjectMarkerDto[] Copy(IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> values) => ClientProtocolJson.Copy(values.ToArray());
    internal static string Signature(IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> values)
        => values.Count == 0 ? "" : ClientProtocolJson.UnorderedSignature(values);
}
