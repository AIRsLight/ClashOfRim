using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal sealed record ClientWorldObjectTargetState(ModWorldMapMarkerDto Marker, bool Allied);
internal interface IClientWorldObjectCommandTarget { ClientWorldObjectTargetState? Read(); }
internal interface IClientWorldObjectCommandRuntime
{
    bool HasActiveRemoteMap { get; }
    void Reject(string key);
}

// Owns choices made against a particular account, Game and world-object identity.
// Observation and transport workflows keep their existing admission/execution.
internal sealed class ClientWorldObjectCommands
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientRequestTracker<Boundary> lifetime;
    private readonly ClientObservationMaps observations;
    private readonly IClientWorldObjectCommandRuntime runtime;
    internal ClientWorldObjectCommands(ClientSessionLifecycle session, IClientSnapshotGame game,
        ClientObservationMaps observations, IClientWorldObjectCommandRuntime runtime)
    {
        this.session = session; this.observations = observations; this.runtime = runtime;
        lifetime = new(session, () =>
        {
            var context = session.ReadContext();
            return new(game.BindCurrentGame(), context.OfflinePassword, context.SteamAuthTicket, session.IsUnavailable);
        }, bound =>
        {
            var context = session.ReadContext();
            return bound.Game.IsCurrent && bound.Password == context.OfflinePassword && bound.Steam == context.SteamAuthTicket
                && bound.Unavailable == session.IsUnavailable;
        }, static () => { });
    }

    internal void Reset() => lifetime.Reset();
    internal Selection? Capture(IClientWorldObjectCommandTarget source)
    {
        using var p = ClientSessionWork.EnterPublication();
        var owner = lifetime.Capture();
        var state = source.Read();
        return state is null || !Owns(owner) ? null : new(this, owner, source, new(ClientProtocolJson.Copy(state.Marker), state.Allied));
    }
    private bool Owns(ClientRequestTracker<Boundary>.Scope owner)
        => !session.IsUnavailable && lifetime.TryApply(owner, static () => { });
    internal sealed record Boundary(IClientSnapshotGameSession Game, string? Password, string? Steam, bool Unavailable);

    internal sealed class Selection
    {
        private readonly ClientWorldObjectCommands commands;
        private readonly ClientRequestTracker<Boundary>.Scope owner;
        private readonly IClientWorldObjectCommandTarget source;
        private readonly ClientWorldObjectTargetState captured;
        private bool expired, presented, committed, executing;
        internal Selection(ClientWorldObjectCommands commands, ClientRequestTracker<Boundary>.Scope owner,
            IClientWorldObjectCommandTarget source, ClientWorldObjectTargetState captured)
        { this.commands = commands; this.owner = owner; this.source = source; this.captured = captured; }

        internal ModWorldMapMarkerDto CopyTarget() => ClientProtocolJson.Copy(captured.Marker);
        internal string? ObservationMode => string.IsNullOrWhiteSpace(captured.Marker.OwnerUserId) || string.IsNullOrWhiteSpace(captured.Marker.OwnerColonyId)
            ? null : captured.Marker.Kind == "TradeableColony" ? captured.Allied ? ClientRemoteMapModes.FriendlyObservation : ClientRemoteMapModes.Scout
                : captured.Marker.Kind == "ActiveRaidTarget" && !string.IsNullOrWhiteSpace(captured.Marker.RelatedEventId) ? ClientRemoteMapModes.RaidObservation : null;
        internal bool ObservationBlocked => commands.runtime.HasActiveRemoteMap;
        internal bool IsCurrent
        {
            get
            {
                using var p = ClientSessionWork.EnterPublication();
                if (expired) return false;
                if (!commands.Owns(owner)) { expired = true; return false; }
                var current = source.Read();
                // Recheck after the engine read, which can invoke compatibility code.
                if (current is null || !SameTarget(captured, current) || !commands.Owns(owner)) expired = true;
                return !expired;
            }
        }
        internal bool TryStep(Action action)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (!IsCurrent) return false;
            action(); return true;
        }
        internal bool TryPresent(Action present)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (presented || committed || executing || !IsCurrent) return false;
            presented = true; present(); return true;
        }
        internal bool TryCommit(Func<bool> validate, Action execute)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (committed || executing || !IsCurrent) return false;
            executing = true;
            try
            {
                if (!validate() || !IsCurrent) return false;
                // Consume before calling the engine: reentry or a throwing launch
                // cannot launch the same menu choice again with another key.
                committed = true; execute(); return true;
            }
            finally { executing = false; }
        }
        internal Task? Observe()
        {
            if (ObservationMode is not { } mode) return null;
            Task? work = null;
            bool invoked = TryCommit(() =>
            {
                if (!ObservationBlocked) return true;
                TryStep(() => commands.runtime.Reject("ClashOfRim.RemoteSessionMap.ActiveSessionBlocksOpen"));
                return false;
            }, () => work = commands.observations.OpenAsync(CopyTarget(), mode));
            // No admission (e.g. a busy client) leaves this still-current button
            // retryable. A successful operation remains owned by observations.
            if (invoked && work is null) committed = false;
            return work;
        }
        private static bool SameTarget(ClientWorldObjectTargetState expected, ClientWorldObjectTargetState actual)
        {
            var a = expected.Marker; var b = actual.Marker;
            return a.MarkerId == b.MarkerId && a.Kind == b.Kind && a.OwnerUserId == b.OwnerUserId && a.OwnerColonyId == b.OwnerColonyId
                && a.WorldObjectId == b.WorldObjectId && a.MapId == b.MapId && a.RelatedEventId == b.RelatedEventId
                && a.Tile == b.Tile && a.TileLayerId == b.TileLayerId && a.CanRaid == b.CanRaid && a.CanTrade == b.CanTrade
                && a.CanReinforce == b.CanReinforce && a.RelationKind == b.RelationKind && expected.Allied == actual.Allied;
        }
    }
}
