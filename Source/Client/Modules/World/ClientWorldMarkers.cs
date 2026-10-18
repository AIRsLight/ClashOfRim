using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientWorldMarkersRuntime { IClientWorldMarkersSession Bind(); }
internal interface IClientWorldMarkersSession
{
    bool IsCurrent { get; }
    bool HasWorldObjects { get; }
    void Enqueue(Action action, Action discard);
    ClientWorldMarkersApplied Apply(IReadOnlyCollection<ModWorldMapMarkerDto> markers, string user);
}
internal sealed record ClientWorldMarkersApplied(bool Changed, string Description);
internal interface IClientWorldMarkersPresentation
{
    string Summary(IReadOnlyCollection<ModWorldMapMarkerDto> markers);
    void Status(string text);
    void Diagnose(string text, bool warning);
}
internal enum ClientColonyTargetKind { Reinforcement, Raid }

// The catalog belongs to the account/server world and survives vanilla creating
// a new Game. Each queued projection belongs to one exact engine world and view.
internal sealed class ClientWorldMarkers
{
    private readonly ClientSessionLifecycle session;
    private readonly IClientWorldIdentity identity;
    private readonly ClientRequestTracker<string> cache;
    private readonly ClientOccupiedColonySites occupied;
    private readonly IClientWorldMarkersRuntime runtime;
    private readonly IClientWorldMarkersPresentation presentation;
    private ModWorldMapMarkerDeliveryDto? delivery;
    private long nextObservation, acceptedObservation;
    private object observationEpoch = new();

    internal ClientWorldMarkers(ClientSessionLifecycle session, IClientWorldIdentity identity, ClientOccupiedColonySites occupied,
        IClientWorldMarkersRuntime runtime, IClientWorldMarkersPresentation presentation)
    {
        this.session = session; this.identity = identity; this.occupied = occupied; this.runtime = runtime; this.presentation = presentation;
        cache = new(session, () => identity.WorldId, id => id == identity.WorldId, () => { delivery = null; acceptedObservation = 0; });
    }
    internal bool GiftsEnabled => cache.Read(() => delivery?.GiftsEnabled ?? true);
    internal bool PvpEnabled => cache.Read(() => delivery?.PvpEnabled ?? true);
    internal List<ModWorldMapMarkerDto> Snapshot => cache.Read(() => CopyMarkers(delivery));
    internal void Reset()
    {
        using var p = ClientSessionWork.EnterPublication();
        observationEpoch = new(); cache.Reset();
    }

    internal Observation CaptureObservation()
    {
        using var publication = ClientSessionWork.EnterPublication();
        return new(cache.Capture(), runtime.Bind(), ++nextObservation, observationEpoch);
    }
    internal sealed record Observation(ClientRequestTracker<string>.Scope Scope, IClientWorldMarkersSession Runtime, long Number, object Epoch);

    private bool SameSession(Observation observation) => ReferenceEquals(observation.Epoch, observationEpoch)
        && observation.Runtime.IsCurrent && session.Owns(observation.Scope.Owner, observation.Scope.Context, false);
    internal bool OwnsSession(Observation observation) => !session.IsUnavailable && SameSession(observation);
    internal bool Owns(Observation observation) => OwnsSession(observation) && cache.TryApply(observation.Scope, static () => { });
    internal bool OwnsLoginSource(Observation observation) => SameSession(observation) && cache.TryApply(observation.Scope, static () => { });
    internal bool HasNewerObservation(Observation observation) => acceptedObservation > observation.Number;
    internal Observation? AdoptWorld(Observation observation, string world)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!OwnsSession(observation) || identity.WorldId != world) return null;
        return observation with { Scope = cache.Capture() };
    }
    internal Observation? AdoptLogin(Observation observation, ClientSessionLifecycle.Login login)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (session.IsUnavailable || !ReferenceEquals(observation.Epoch, observationEpoch) || !observation.Runtime.IsCurrent || identity.WorldId != observation.Scope.Boundary
            || !session.Continues(login, observation.Scope.Owner, observation.Scope.Context)) return null;
        return observation with { Scope = cache.Capture() };
    }
    internal Observation? AdoptColony(Observation observation, string colony)
    {
        using var p = ClientSessionWork.EnterPublication();
        var context = observation.Scope.Context;
        var expected = context.WithColony(colony, context.CurrentSnapshotId);
        if (!ReferenceEquals(observation.Epoch, observationEpoch) || session.IsUnavailable || !observation.Runtime.IsCurrent
            || identity.WorldId != observation.Scope.Boundary || !session.Owns(observation.Scope.Owner, expected, false)) return null;
        return observation with { Scope = cache.Capture() };
    }

    internal void Observe(Observation observation, ModWorldMapMarkerDeliveryDto? source, bool immediately = false, string? reason = null, Func<bool>? current = null)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = observation.Scope; var bound = observation.Runtime;
        bool Owned() => Owns(observation);
        var captured = source is null ? new() : ClientProtocolJson.Copy(source);
        captured.Markers ??= new();
        if (current?.Invoke() == false || !Owned() || observation.Number <= acceptedObservation) return;
        acceptedObservation = observation.Number;
        delivery = captured;
        bool Cached() => Owned() && ReferenceEquals(delivery, captured);
        bool Current() => current?.Invoke() != false && Cached();
        occupied.Merge(captured.Markers);
        if (!Current()) return;
        string label = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason!;
        presentation.Diagnose("[ClashOfRim] Captured world map markers from " + label
            + ": user=" + captured.UserId + ", generatedAt=" + captured.GeneratedAtUtc + ", markers=" + captured.Markers.Count, false);
        if (!Current()) return;
        bool consumed = false;
        void Apply()
        {
            using var p = ClientSessionWork.EnterPublication();
            if (consumed) return; consumed = true;
            // Once accepted, queued engine work belongs to this cache revision,
            // not the HTTP request that can finish before the queue is drained.
            Project(scope, bound, captured, immediately ? Current : Cached, label, updateStatus: true);
        }
        if (immediately) Apply();
        else bound.Enqueue(Apply, () => consumed = true);
    }

    internal void ApplyCached()
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = cache.Capture(); var captured = delivery;
        // No delivery is distinct from an authoritative empty delivery. The latter
        // must remove old engine markers after a world/page transition as well.
        if (captured is null) return;
        var bound = runtime.Bind();
        bool Current() => !session.IsUnavailable && bound.IsCurrent && cache.TryApply(scope, static () => { }) && ReferenceEquals(delivery, captured);
        Project(scope, bound, captured, Current, "cached-world", updateStatus: false);
    }

    private void Project(ClientRequestTracker<string>.Scope scope, IClientWorldMarkersSession bound,
        ModWorldMapMarkerDeliveryDto captured, Func<bool> current, string label, bool updateStatus)
    {
        if (!current()) return;
        if (updateStatus)
        {
            string text = presentation.Summary(CopyMarkers(captured));
            if (!current()) return;
            presentation.Status(text);
            if (!current()) return;
        }
        bool hasWorld = bound.HasWorldObjects;
        if (!current() || string.IsNullOrWhiteSpace(scope.Context.UserId)) return;
        if (!hasWorld)
        {
            if (updateStatus) presentation.Diagnose("[ClashOfRim] World objects unavailable; markers retained for the world page.", true);
            return;
        }
        var result = bound.Apply(CopyMarkers(captured), scope.Context.UserId);
        if (current() && (updateStatus || result.Changed))
            presentation.Diagnose("[ClashOfRim] Applied world map markers from " + label + ": " + result.Description, false);
    }

    internal IReadOnlyList<ModWorldMapMarkerDto> FindTargets(int tile, int layer, ClientColonyTargetKind kind)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = cache.Capture(); var context = session.ReadContext();
        if (session.IsUnavailable || !context.IsConfigured || string.IsNullOrWhiteSpace(context.CurrentSnapshotId)
            || (kind == ClientColonyTargetKind.Raid && !PvpEnabled)) return Array.Empty<ModWorldMapMarkerDto>();
        var selected = CopyMarkers(delivery)
            .Where(marker => kind == ClientColonyTargetKind.Raid ? marker.CanRaid : marker.CanReinforce)
            .Where(marker => marker.Tile == tile && marker.TileLayerId == layer)
            .Where(marker => marker.OwnerUserId != context.UserId || marker.OwnerColonyId != context.ColonyId)
            .Where(marker => !string.IsNullOrWhiteSpace(marker.OwnerUserId) && !string.IsNullOrWhiteSpace(marker.OwnerColonyId)
                && !string.IsNullOrWhiteSpace(marker.SnapshotId) && !string.IsNullOrWhiteSpace(marker.MapId)
                && (kind != ClientColonyTargetKind.Raid || !string.IsNullOrWhiteSpace(marker.WorldObjectId)))
            .OrderBy(marker => marker.OwnerUserId, StringComparer.Ordinal).ThenBy(marker => marker.OwnerColonyId, StringComparer.Ordinal).ToList();
        return cache.TryApply(scope, static () => { }) ? selected : Array.Empty<ModWorldMapMarkerDto>();
    }

    private static List<ModWorldMapMarkerDto> CopyMarkers(ModWorldMapMarkerDeliveryDto? value)
        => value is null ? new() : ClientProtocolJson.Copy(value.Markers);
}
