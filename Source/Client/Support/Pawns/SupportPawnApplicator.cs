using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Quests;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.Support;

internal static class SupportPawnApplicator
{
    public static bool IsSupportPawnDetail(ModEventDetailDto detail)
    {
        return detail.EventType == ServerEventType.SupportPawn
            && !string.IsNullOrWhiteSpace(detail.PayloadSummary);
    }

    public static bool IsReturnToSenderDetail(ModEventDetailDto detail)
    {
        if (!IsSupportPawnDetail(detail))
        {
            return false;
        }

        try
        {
            return SupportPawnPayloadReader.Read(detail.PayloadSummary).ReturnToSender;
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool IsRejectableSupportPawnDetail(ModEventDetailDto detail)
    {
        if (!IsSupportPawnDetail(detail))
        {
            return false;
        }

        try
        {
            SupportPawnPayloadSummary payload = SupportPawnPayloadReader.Read(detail.PayloadSummary);
            return payload.TemporaryControl && !payload.ReturnToSender;
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            return false;
        }
    }

    public static SupportPawnApplicationResult ApplyToCurrentMap(ModEventDetailDto detail)
    {
        if (!IsSupportPawnDetail(detail))
        {
            return SupportPawnApplicationResult.Failed(ClashOfRimText.Key("ClashOfRim.Support.ApplyNotSupportPawn"));
        }

        SupportPawnPayloadSummary payload;
        try
        {
            payload = SupportPawnPayloadReader.Read(detail.PayloadSummary);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            return SupportPawnApplicationResult.Failed(
                ClashOfRimText.Key("ClashOfRim.Support.ApplyPayloadParseFailed", ex.Message.Named("MESSAGE")));
        }

        if (payload.ReturnToSender)
        {
            return ApplyReturnToSenderCaravan(detail, payload);
        }

        Map? map = Find.CurrentMap;
        if (map is null)
        {
            return SupportPawnApplicationResult.Failed(ClashOfRimText.Key("ClashOfRim.Support.ApplyNoMap"));
        }

        string currentMapId = "Map_" + map.uniqueID;
        string? targetMapId = detail.TargetContext?.MapUniqueId;
        if (!string.IsNullOrWhiteSpace(targetMapId)
            && !string.Equals(currentMapId, targetMapId, StringComparison.Ordinal))
        {
            return SupportPawnApplicationResult.Failed(
                ClashOfRimText.Key(
                    "ClashOfRim.Support.ApplyMapMismatch",
                    currentMapId.Named("CURRENT"),
                    targetMapId.Named("TARGET")));
        }

        if (!TryGeneratePawn(payload, out Pawn? pawn, out string generateMessage) || pawn is null)
        {
            return SupportPawnApplicationResult.Failed(generateMessage, terminalFailure: true);
        }

        IntVec3 cell = PawnExchangePlacementService.SpawnAtMapEdge(pawn, map);
        SendSupportArrivalLetter(pawn, payload.PermanentSupport);
        if (payload.PermanentSupport)
        {
            return SupportPawnApplicationResult.Success(pawn.LabelShort, cell.ToString());
        }

        long? effectiveExpiresAtGameTicks = ClashManagedQuestTimingUtility.CurrentGameTicks
            + Math.Max(3, Math.Min(30, payload.SupportDurationDays ?? 3)) * (long)ClashManagedQuestTimingUtility.TicksPerDay;
        var assignment = new ActiveSupportPawnAssignment
        {
            EventId = detail.EventId,
            PawnGlobalKey = payload.PawnGlobalKey,
            PawnThingId = pawn.ThingID,
            PawnLabel = pawn.LabelShort,
            OwnerUserId = detail.Actor?.UserId ?? string.Empty,
            OwnerColonyId = detail.Actor?.ColonyId,
            OwnerSnapshotId = detail.Actor?.SnapshotId,
            OriginalFactionDefName = payload.PawnPackage?.Identity?.FactionDef ?? payload.PawnReference?.Faction,
            OriginalFactionName = detail.Actor?.UserId,
            SourceTile = payload.SourceTile,
            SourceCaravanLoadId = payload.SourceCaravanLoadId,
            PawnReferenceMetadata = CopyPawnReferenceMetadata(payload),
            PermanentSupport = payload.PermanentSupport,
            SupportDurationDays = payload.SupportDurationDays,
            ExpiresAtGameTicks = effectiveExpiresAtGameTicks,
            AutoReturnOnSettlement = payload.AutoReturnOnSettlement
        };
        ClashOfRimGameComponent.RegisterSupportAssignment(assignment);
        ClashSupportPawnQuestUtility.CreateOrUpdateSupportQuest(assignment);
        return SupportPawnApplicationResult.Success(pawn.LabelShort, cell.ToString());
    }

    private static Dictionary<string, string?> CopyPawnReferenceMetadata(SupportPawnPayloadSummary payload)
    {
        return payload.PawnReference?.Metadata is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(payload.PawnReference.Metadata, StringComparer.Ordinal);
    }

    public static SupportPawnApplicationResult SpawnRaidAttackerOnCurrentMap(
        string pawnGlobalKey,
        ModPawnExchangePackageDto package,
        out string pawnThingId)
    {
        pawnThingId = string.Empty;
        Map? map = Find.CurrentMap;
        if (map is null)
        {
            return SupportPawnApplicationResult.Failed(ClashOfRimText.Key("ClashOfRim.Support.ApplyNoMapRaidAttacker"));
        }

        SupportPawnPayloadSummary payload = FromPackage(pawnGlobalKey, package);
        if (!TryGeneratePawn(payload, out Pawn? pawn, out string generateMessage) || pawn is null)
        {
            return SupportPawnApplicationResult.Failed(generateMessage, terminalFailure: true);
        }

        IntVec3 cell = PawnExchangePlacementService.SpawnAtMapEdge(pawn, map);
        pawnThingId = pawn.ThingID;
        return SupportPawnApplicationResult.Success(pawn.LabelShort, cell.ToString());
    }

    private static SupportPawnApplicationResult ApplyReturnToSenderCaravan(
        ModEventDetailDto detail,
        SupportPawnPayloadSummary payload)
    {
        if (IsSupportLossReturn(payload))
        {
            return ApplySupportPawnLoss(payload);
        }

        int? tile = detail.TargetContext?.Tile ?? payload.SourceTile;
        if (tile is null || tile < 0)
        {
            return SupportPawnApplicationResult.Failed(ClashOfRimText.Key("ClashOfRim.Support.ApplyReturnTileMissing"));
        }

        DetachedSupportPawnContext detachedContext =
            SupportPawnWorldPawnContextUtility.DetachReturnContext(payload.PawnGlobalKey);
        if (!TryGeneratePawn(payload, out Pawn? pawn, out string generateMessage) || pawn is null)
        {
            SupportPawnWorldPawnContextUtility.RestoreDetachedContext(detachedContext);
            return SupportPawnApplicationResult.Failed(generateMessage, terminalFailure: true);
        }

        SupportPawnWorldPawnContextUtility.ReplaceDetachedContextWithReturnedPawn(
            detachedContext,
            pawn,
            payload.PawnGlobalKey);
        NormalizeReturnedSupportPawn(pawn);
        Caravan caravan = PawnExchangePlacementService.CreatePlayerCaravan(pawn, tile.Value);
        SendSupportReturnLetter(pawn, caravan, payload);
        return SupportPawnApplicationResult.SuccessReturnCaravan(pawn.LabelShort, caravan.Label, tile.Value);
    }

    private static bool IsSupportLossReturn(SupportPawnPayloadSummary payload)
    {
        if (!payload.ReturnToSender || payload.PawnPackage is not null)
        {
            return false;
        }

        return string.Equals(payload.ReturnReason, "RaidSettlementLost", StringComparison.Ordinal)
            || string.Equals(payload.ReturnReason, "MapUnloaded", StringComparison.Ordinal)
            || string.Equals(payload.ReturnReason, "SnapshotDeath", StringComparison.Ordinal);
    }

    private static SupportPawnApplicationResult ApplySupportPawnLoss(SupportPawnPayloadSummary payload)
    {
        if (!SupportPawnWorldPawnContextUtility.TryMarkSupportPawnLost(
                payload.PawnGlobalKey,
                payload.PawnName,
                payload.ReturnReason,
                out Pawn? pawn,
                out string pawnLabel,
                out string message))
        {
            return SupportPawnApplicationResult.Failed(message);
        }

        SendSupportLossLetter(pawn, pawnLabel, payload);
        return SupportPawnApplicationResult.SuccessLoss(pawnLabel, message);
    }

    private static void SendSupportArrivalLetter(Pawn pawn, bool permanentSupport)
    {
        try
        {
            Find.LetterStack.ReceiveLetter(
                ClashOfRimText.Key(permanentSupport
                    ? "ClashOfRim.Support.PermanentArrivalLetterLabel"
                    : "ClashOfRim.Support.ArrivalLetterLabel"),
                ClashOfRimText.Key(permanentSupport
                    ? "ClashOfRim.Support.PermanentArrivalLetterText"
                    : "ClashOfRim.Support.ArrivalLetterText",
                    pawn.LabelShort.Named("PAWN")),
                LetterDefOf.PositiveEvent,
                pawn);
        }
        catch (Exception ex)
        {
            Log.Warning($"[ClashOfRim][Support] arrival letter failed pawn={pawn?.ThingID ?? "<null>"} exception={ex}");
        }
    }

    private static void SendSupportReturnLetter(Pawn pawn, Caravan caravan, SupportPawnPayloadSummary payload)
    {
        try
        {
            string reason = string.IsNullOrWhiteSpace(payload.ReturnReason)
                ? ClashOfRimText.Key("ClashOfRim.Support.ReturnReasonDefault")
                : payload.ReturnReason!;
            Find.LetterStack.ReceiveLetter(
                ClashOfRimText.Key("ClashOfRim.Support.ReturnArrivalLetterLabel"),
                ClashOfRimText.Key(
                    "ClashOfRim.Support.ReturnArrivalLetterText",
                    pawn.LabelShort.Named("PAWN"),
                    reason.Named("REASON")),
                LetterDefOf.PositiveEvent,
                caravan);
        }
        catch (Exception ex)
        {
            Log.Warning($"[ClashOfRim][Support] return letter failed pawn={pawn?.ThingID ?? "<null>"} caravan={caravan?.ID ?? -1} exception={ex}");
        }
    }

    private static void SendSupportLossLetter(Pawn? pawn, string pawnLabel, SupportPawnPayloadSummary payload)
    {
        try
        {
            string reason = string.IsNullOrWhiteSpace(payload.ReturnReason)
                ? ClashOfRimText.Key("ClashOfRim.Support.LossReasonDefault")
                : payload.ReturnReason!;
            Find.LetterStack.ReceiveLetter(
                ClashOfRimText.Key("ClashOfRim.Support.LossLetterLabel"),
                ClashOfRimText.Key(
                    "ClashOfRim.Support.LossLetterText",
                    pawnLabel.Named("PAWN"),
                    reason.Named("REASON")),
                LetterDefOf.NegativeEvent,
                pawn);
        }
        catch (Exception ex)
        {
            Log.Warning($"[ClashOfRim][Support] loss letter failed pawn={pawn?.ThingID ?? "<null>"} exception={ex}");
        }
    }

    private static void NormalizeReturnedSupportPawn(Pawn pawn)
    {
        if (pawn is null)
        {
            return;
        }

        Lord? lord = pawn.GetLord();
        lord?.Notify_PawnLost(pawn, PawnLostCondition.ForcedByQuest, null);
        pawn.mindState.duty = null;

        if (pawn.drafter is not null)
        {
            pawn.drafter.Drafted = false;
        }

        if (pawn.jobs?.curJob is not null)
        {
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false, canReturnToPool: true);
        }

        if (pawn.guest is not null)
        {
            pawn.guest.SetGuestStatus(null, GuestStatus.Guest);
        }

        if (pawn.Faction != Faction.OfPlayer)
        {
            pawn.SetFaction(Faction.OfPlayer);
        }

        pawn.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
        pawn.ClearAllReservations(releaseDestinationsOnlyIfObsolete: false);
    }

    private static bool TryGeneratePawn(SupportPawnPayloadSummary payload, out Pawn? pawn, out string message)
    {
        pawn = null;
        if (SupportPawnScribeRestorer.TryRestore(payload, out Pawn? restored, out string restoreMessage)
            && restored is not null)
        {
            ClashLog.Message("[ClashOfRim] " + restoreMessage);
            pawn = restored;
            message = restoreMessage;
            return true;
        }

        message = string.IsNullOrWhiteSpace(restoreMessage)
            ? ClashOfRimText.Key("ClashOfRim.Support.ApplyRestoreFailed")
            : restoreMessage;
        Log.Warning("[ClashOfRim] " + message + " Support pawn application will fail.");
        return false;
    }

    private static SupportPawnPayloadSummary FromPackage(string pawnGlobalKey, ModPawnExchangePackageDto package)
    {
        ClashOfRimCompatibilityApi.NormalizePawnExchangePackage(package);
        return new SupportPawnPayloadSummary
        {
            PawnGlobalKey = pawnGlobalKey ?? string.Empty,
            SourceSnapshotId = package?.Reference?.SourceSnapshotId ?? string.Empty,
            PawnName = package?.Appearance?.DisplayName,
            TemporaryControl = true,
            PawnReference = new SupportPawnReferenceSummary
            {
                Faction = package?.Reference?.Faction,
                Metadata = package?.Reference?.Metadata ?? new Dictionary<string, string?>()
            },
            PawnPackage = new SupportPawnExchangePackageSummary
            {
                PackageVersion = package?.PackageVersion ?? 1,
                Identity = new SupportPawnExchangeIdentitySummary
                {
                    ThingDef = package?.Identity?.ThingDef,
                    PawnKindDef = package?.Identity?.PawnKindDef,
                    FactionDef = package?.Identity?.FactionDef,
                    Gender = package?.Identity?.Gender
                },
                Appearance = new SupportPawnExchangeAppearanceSummary
                {
                    DisplayName = package?.Appearance?.DisplayName
                },
                Extensions = package?.Extensions.Select(extension =>
                    new SupportPawnExchangeExtensionPackageSummary
                    {
                        ProviderId = extension.ProviderId,
                        Kind = extension.Kind,
                        Metadata = extension.Metadata,
                        PayloadJson = extension.PayloadJson
                    }).ToList() ?? new List<SupportPawnExchangeExtensionPackageSummary>(),
                Relationships = package?.Relationships.Select(relationship =>
                    new SupportPawnExchangeRelationshipStubSummary
                    {
                        OtherPawnGlobalId = relationship.OtherPawnGlobalId,
                        OtherPawnName = relationship.OtherPawnName,
                        OtherPawnDead = relationship.OtherPawnDead,
                        RelationDef = relationship.RelationDef
                    }).ToList() ?? new List<SupportPawnExchangeRelationshipStubSummary>(),
                Scribe = package?.Scribe is null
                    ? null
                    : new SupportPawnScribePayloadSummary
                    {
                        Xml = package.Scribe.Xml,
                        XmlSha256 = package.Scribe.XmlSha256,
                        PawnReferenceReplacements = package.Scribe.PawnReferenceReplacements.Select(replacement =>
                            new SupportPawnScribePawnReferenceReplacementSummary
                            {
                                SourceLoadId = replacement.SourceLoadId,
                                PlaceholderLoadId = replacement.PlaceholderLoadId
                            }).ToList()
                    }
            }
        };
    }

    public static IntVec3 FindMapEdgeLandingCell(Map map)
    {
        return PawnExchangePlacementService.FindMapEdgeLandingCell(map);
    }
}

internal sealed class SupportPawnApplicationResult
{
    private SupportPawnApplicationResult(bool applied, string message, string? pawnLabel, string? cell, bool terminalFailure)
    {
        Applied = applied;
        Message = message;
        PawnLabel = pawnLabel;
        Cell = cell;
        TerminalFailure = terminalFailure;
    }

    public bool Applied { get; }

    public string Message { get; }

    public string? PawnLabel { get; }

    public string? Cell { get; }

    public bool TerminalFailure { get; }

    public static SupportPawnApplicationResult Success(string pawnLabel, string cell)
    {
        return new SupportPawnApplicationResult(
            true,
            ClashOfRimText.Key("ClashOfRim.Support.ApplyArrived", pawnLabel.Named("PAWN"), cell.Named("CELL")),
            pawnLabel,
            cell,
            terminalFailure: false);
    }

    public static SupportPawnApplicationResult SuccessReturnCaravan(string pawnLabel, string caravanLabel, int tile)
    {
        return new SupportPawnApplicationResult(
            true,
            ClashOfRimText.Key(
                "ClashOfRim.Support.ApplyReturnCaravanCreated",
                pawnLabel.Named("PAWN"),
                tile.Named("TILE"),
                caravanLabel.Named("CARAVAN")),
            pawnLabel,
            tile.ToString(),
            terminalFailure: false);
    }

    public static SupportPawnApplicationResult SuccessLoss(string pawnLabel, string message)
    {
        return new SupportPawnApplicationResult(
            true,
            message,
            pawnLabel,
            null,
            terminalFailure: false);
    }

    public static SupportPawnApplicationResult Failed(string message, bool terminalFailure = false)
    {
        return new SupportPawnApplicationResult(false, message, null, null, terminalFailure);
    }
}
