using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility.RemoteMaps;

internal sealed class RemoteMapLifecycleCompatibility
{
    private readonly List<RaidCaravanMapEntryHandler> RaidCaravanMapEntryHandlers = new();
    private readonly List<RemoteDefenderMapPreparedHandler> RemoteDefenderMapPreparedHandlers = new();
    private readonly List<RemoteMapLoadedHandler> RemoteMapLoadedHandlers = new();
    private readonly List<RemoteSessionPawnCleanupHandler> RemoteSessionPawnCleanupHandlers = new();
    private readonly List<Func<Pawn, bool>> DefaultPawnAiFilters = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal RemoteMapLifecycleCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterRemotePawnDefaultAiFilter(Func<Pawn, bool> filter) =>
        Registrations.RegisterUnique(DefaultPawnAiFilters, filter);

    internal bool CanUseDefaultRemotePawnAi(Pawn pawn)
    {
        foreach (var filter in DefaultPawnAiFilters.ToList())
        {
            if (Registrations.TryInvokeCompatibilityCallback(
                    nameof(CanUseDefaultRemotePawnAi), filter, () => filter(pawn), out bool allowed) && !allowed)
                return false;
        }
        return true;
    }

    internal void RegisterRaidCaravanMapEntryHandler(RaidCaravanMapEntryHandler handler)
    {
        Registrations.RegisterUnique(RaidCaravanMapEntryHandlers, handler);
    }

    internal void RegisterRemoteDefenderMapPreparedHandler(RemoteDefenderMapPreparedHandler handler)
    {
        Registrations.RegisterUnique(RemoteDefenderMapPreparedHandlers, handler);
    }

    internal void RegisterRemoteMapLoadedHandler(RemoteMapLoadedHandler handler)
    {
        Registrations.RegisterUnique(RemoteMapLoadedHandlers, handler);
    }

    internal void RegisterRemoteSessionPawnCleanupHandler(RemoteSessionPawnCleanupHandler handler)
    {
        Registrations.RegisterUnique(RemoteSessionPawnCleanupHandlers, handler);
    }

    internal RaidCaravanMapEntryResult TryEnterRaidCaravanMap(Caravan caravan, Map map, IReadOnlyList<Pawn> attackPawns)
    {
        if (RaidCaravanMapEntryHandlers.Count == 0)
        {
            return RaidCaravanMapEntryResult.NotHandled;
        }

        IReadOnlyList<Pawn> safeAttackPawns = attackPawns ?? new List<Pawn>();
        foreach (RaidCaravanMapEntryHandler handler in RaidCaravanMapEntryHandlers.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(TryEnterRaidCaravanMap),
                    handler,
                    () => handler(caravan, map, safeAttackPawns),
                    out RaidCaravanMapEntryResult result))
            {
                continue;
            }

            if (result.Kind != RaidCaravanMapEntryResultKind.NotHandled)
            {
                return result;
            }
        }

        return RaidCaravanMapEntryResult.NotHandled;
    }

    internal void NotifyRemoteDefenderMapPrepared(Map map, Faction defenderFaction)
    {
        if (RemoteDefenderMapPreparedHandlers.Count == 0)
        {
            return;
        }

        foreach (RemoteDefenderMapPreparedHandler handler in RemoteDefenderMapPreparedHandlers.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(NotifyRemoteDefenderMapPrepared),
                handler,
                () => handler(map, defenderFaction));
        }
    }

    internal void NotifyRemoteMapLoaded(Map map, RemoteSessionMapParent carrier, string scope, ModSnapshotPackageMetadataDto package)
    {
        if (RemoteMapLoadedHandlers.Count == 0)
        {
            return;
        }

        foreach (RemoteMapLoadedHandler handler in RemoteMapLoadedHandlers.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(NotifyRemoteMapLoaded),
                handler,
                () => handler(map, carrier, scope, package));
        }
    }

    internal bool TryCleanupRemoteSessionPawn(Pawn pawn, string reason)
    {
        if (pawn is null || RemoteSessionPawnCleanupHandlers.Count == 0)
        {
            return false;
        }

        foreach (RemoteSessionPawnCleanupHandler handler in RemoteSessionPawnCleanupHandlers.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(TryCleanupRemoteSessionPawn),
                    handler,
                    () => handler(pawn, reason),
                    out bool handled))
            {
                continue;
            }

            if (handled)
            {
                return true;
            }
        }

        return false;
    }
}
