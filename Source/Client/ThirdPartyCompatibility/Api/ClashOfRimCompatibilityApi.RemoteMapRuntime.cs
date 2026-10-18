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
    public static RaidCaravanMapEntryResult TryEnterRaidCaravanMap(Caravan caravan, Map map, IReadOnlyList<Pawn> attackPawns) =>
        RemoteMapLifecycle.TryEnterRaidCaravanMap(caravan, map, attackPawns);

    public static void NotifyRemoteDefenderMapPrepared(Map map, Faction defenderFaction) =>
        RemoteMapLifecycle.NotifyRemoteDefenderMapPrepared(map, defenderFaction);

    public static void NotifyRemoteMapLoaded(Map map, RemoteSessionMapParent carrier, string scope, ModSnapshotPackageMetadataDto package) =>
        RemoteMapLifecycle.NotifyRemoteMapLoaded(map, carrier, scope, package);

    public static IReadOnlyList<ILoadReferenceable> RewriteRemoteMapProjectionReferences(
        ModSnapshotPackageMetadataDto package,
        XDocument sourceDocument,
        XElement projectionElement) =>
        RemoteMapProjection.RewriteRemoteMapProjectionReferences(package, sourceDocument, projectionElement);

    public static void SanitizeRemoteMapProjection(
        ModSnapshotPackageMetadataDto package,
        XElement mapElement,
        XElement referencePawnsElement) =>
        RemoteMapProjection.SanitizeRemoteMapProjection(package, mapElement, referencePawnsElement);

    public static bool TryCleanupRemoteSessionPawn(Pawn pawn, string reason) =>
        RemoteMapLifecycle.TryCleanupRemoteSessionPawn(pawn, reason);

    public static int SanitizeSnapshotSave(XDocument document) =>
        SnapshotSanitization.SanitizeSnapshotSave(document);

    public static void ApplyRaidDefenderProxyPawnProtection(Pawn pawn, Faction defenderFaction) =>
        RaidDefenderLootProtectionUtility.ApplyProxyPawnProtection(pawn, defenderFaction);
}
