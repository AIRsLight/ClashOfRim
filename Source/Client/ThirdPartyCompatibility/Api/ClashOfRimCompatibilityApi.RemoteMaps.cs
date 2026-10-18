using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Raids;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    public static void RegisterRaidCaravanMapEntryHandler(RaidCaravanMapEntryHandler handler) =>
        RemoteMapLifecycle.RegisterRaidCaravanMapEntryHandler(handler);

    public static void RegisterRemoteDefenderMapPreparedHandler(RemoteDefenderMapPreparedHandler handler) =>
        RemoteMapLifecycle.RegisterRemoteDefenderMapPreparedHandler(handler);

    public static void RegisterRemoteMapLoadedHandler(RemoteMapLoadedHandler handler) =>
        RemoteMapLifecycle.RegisterRemoteMapLoadedHandler(handler);

    public static void RegisterRemoteMapProjectionReferenceRewriter(RemoteMapProjectionReferenceRewriter rewriter) =>
        RemoteMapProjection.RegisterRemoteMapProjectionReferenceRewriter(rewriter);

    public static void RegisterRemoteMapProjectionSanitizer(RemoteMapProjectionSanitizer sanitizer) =>
        RemoteMapProjection.RegisterRemoteMapProjectionSanitizer(sanitizer);

    public static void RegisterRemoteSessionPawnCleanupHandler(RemoteSessionPawnCleanupHandler handler) =>
        RemoteMapLifecycle.RegisterRemoteSessionPawnCleanupHandler(handler);

    public static void RegisterRemoteMapIntegerLoadIdRewriter(
        string loadIdPrefix,
        Func<XElement, bool> predicate,
        Func<int> nextId) =>
        RemoteMapProjection.RegisterRemoteMapIntegerLoadIdRewriter(loadIdPrefix, predicate, nextId);

    public static IReadOnlyList<RemoteMapIntegerLoadIdRewriterRegistration> GetRemoteMapIntegerLoadIdRewriters() =>
        RemoteMapProjection.GetRemoteMapIntegerLoadIdRewriters();

    public static void RegisterRemoteMapAreaLoadIdProvider(RemoteMapAreaLoadIdProvider provider) =>
        RemoteMapProjection.RegisterRemoteMapAreaLoadIdProvider(provider);

    public static string? BuildRemoteMapAreaLoadId(XElement area, int id) =>
        RemoteMapProjection.BuildRemoteMapAreaLoadId(area, id);

    public static void RegisterSnapshotSaveSanitizer(CompatibilitySnapshotSaveSanitizer sanitizer) =>
        SnapshotSanitization.RegisterSnapshotSaveSanitizer(sanitizer);

    public static void RegisterPlayerColonySiteRegistrationSuppressor(PlayerColonySiteRegistrationSuppressor suppressor) =>
        ColonySite.RegisterPlayerColonySiteRegistrationSuppressor(suppressor);

    public static bool IsPlayerColonySiteRegistrationSuppressedByCompatibility() =>
        ColonySite.IsPlayerColonySiteRegistrationSuppressedByCompatibility();
}
