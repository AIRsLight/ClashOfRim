using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static class ProtocolDtoMapper
{
    public static EventQueueSummaryDto ToDto(EventQueueSummary summary)
    {
        return new EventQueueSummaryDto(
            summary.DirectlyProcessable.Select(ToDto).ToList(),
            summary.WaitingForConfirmation.Select(ToDto).ToList(),
            summary.DeliveredUnconfirmed.Select(ToDto).ToList(),
            summary.Conflicts.Select(ToDto).ToList(),
            summary.Rejected.Select(ToDto).ToList());
    }

    public static WorldMapMarkerDeliveryDto ToDto(WorldMapMarkerDelivery delivery)
    {
        return new WorldMapMarkerDeliveryDto(
            delivery.UserId,
            delivery.GeneratedAtUtc,
            delivery.Markers.Select(ToDto).ToList(),
            delivery.GiftsEnabled,
            delivery.PvpEnabled);
    }

    public static WorldMapMarkerDto ToDto(WorldMapMarker marker)
    {
        return new WorldMapMarkerDto(
            marker.MarkerId,
            marker.Kind.ToString(),
            marker.OwnerUserId,
            marker.ColonyId,
            marker.WorldObjectId ?? string.Empty,
            marker.MapUniqueId,
            marker.SnapshotId ?? marker.RaidAvailability?.DefenderSnapshotId,
            marker.Tile,
            marker.Label,
            marker.RelatedEventId,
            marker.RaidAvailability?.CanRaid ?? false,
            marker.TradeEnabled,
            marker.ReinforcementEnabled,
            ResolveRaidUnavailableReason(marker.RaidAvailability),
            ResolveRaidUnavailableUntilUtc(marker.RaidAvailability),
            marker.IconDefName,
            marker.RelationKind,
            marker.OwnerOnline,
            marker.OwnerLastSeenAtUtc,
            marker.OwnerFactionName,
            marker.PathTiles,
            marker.Appearance,
            marker.TileLayerId);
    }

    private static string? ResolveRaidUnavailableReason(RaidAvailabilitySummary? availability)
    {
        if (availability is null || availability.CanRaid)
        {
            return null;
        }

        IReadOnlyList<RaidEligibilityFailureReason> reasons = availability.DisabledReasons;
        if (reasons.Contains(RaidEligibilityFailureReason.DefenderWealthBelowMinimum))
        {
            return "WealthBelowMinimum";
        }

        if (reasons.Contains(RaidEligibilityFailureReason.CooldownActive))
        {
            return "CooldownActive";
        }

        return reasons.Contains(RaidEligibilityFailureReason.DefenderOnline)
            ? "DefenderOnline"
            : null;
    }

    private static DateTimeOffset? ResolveRaidUnavailableUntilUtc(RaidAvailabilitySummary? availability)
    {
        if (availability is null || availability.CanRaid)
        {
            return null;
        }

        return availability.DisabledReasons.Contains(RaidEligibilityFailureReason.CooldownActive)
            ? availability.CooldownUntilUtc
            : null;
    }

    public static SaveSnapshotPackage ToSaveSnapshotPackage(SnapshotPackageMetadataDto dto, byte[] payload)
    {
        SnapshotPayloadEncoding encoding = Enum.Parse<SnapshotPayloadEncoding>(dto.PayloadEncoding, ignoreCase: true);
        var identity = new SnapshotIdentity(dto.OwnerId, dto.ColonyId, dto.SnapshotId);
        var envelope = new SaveSnapshotEnvelope(
            dto.PackageVersion,
            identity,
            DateTimeOffset.UtcNow,
            $"{dto.SnapshotId}.rws",
            dto.RimWorldVersion,
            encoding,
            dto.OriginalSaveBytes,
            dto.PayloadBytes,
            dto.OriginalSha256,
            dto.PayloadSha256,
            dto.PreviousSnapshotId,
            dto.LineageToken,
            dto.NextLineageToken,
            dto.GameTicks,
            NormalizeDefenderThreatPoints(dto.DefenderThreatPoints));

        return new SaveSnapshotPackage(
            envelope,
            payload,
            EmptyIndex(identity));
    }

    public static SnapshotPackageMetadataDto ToMetadataDto(SaveSnapshotPackage package)
    {
        SaveSnapshotEnvelope envelope = package.Envelope;
        SnapshotIdentity identity = envelope.Identity;
        return new SnapshotPackageMetadataDto(
            envelope.PackageVersion,
            identity.OwnerId!,
            identity.ColonyId!,
            identity.SnapshotId!,
            envelope.RimWorldVersion,
            envelope.PayloadEncoding.ToString(),
            envelope.OriginalSaveBytes,
            envelope.PayloadBytes,
            envelope.OriginalSha256,
            envelope.PayloadSha256,
            envelope.PreviousSnapshotId,
            envelope.LineageToken,
            envelope.NextLineageToken,
            envelope.GameTicks,
            defenderThreatPoints: envelope.DefenderThreatPoints);
    }

    private static float? NormalizeDefenderThreatPoints(float? value)
    {
        return value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value)
            ? Math.Clamp(value.Value, 0f, 10000f)
            : null;
    }

    public static EventDetailDto ToDetailDto(AuthoritativeEvent ledgerEvent)
    {
        return new EventDetailDto(
            ledgerEvent.EventId,
            ledgerEvent.Type,
            ledgerEvent.Status.ToString(),
            ToProtocolIdentity(ledgerEvent.Actor),
            ToProtocolIdentity(ledgerEvent.Target),
            ToDto(ledgerEvent.TargetContext),
            ToPayloadType(ledgerEvent.Payload),
            PayloadSummary(ledgerEvent.Payload));
    }

    private static EventReferenceDto ToDto(EventQueueItem item)
    {
        return new EventReferenceDto(
            item.EventId,
            item.Type,
            item.Status.ToString(),
            ToDeliverySemantics(item),
            item.RequiresClientApplication
            && (item.Group == EventQueueGroupKind.DeliveredUnconfirmed ||
                item.Group == EventQueueGroupKind.DirectlyProcessable));
    }

    private static EventPayloadType ToPayloadType(LedgerEventPayload payload)
    {
        return payload switch
        {
            RaidEventPayload => EventPayloadType.Raid,
            ItemDeliveryEventPayload => EventPayloadType.ItemDelivery,
            TradeEventPayload => EventPayloadType.Trade,
            SupportPawnEventPayload => EventPayloadType.SupportPawn,
            AllianceRequestEventPayload => EventPayloadType.AllianceRequest,
            AllianceCancellationEventPayload => EventPayloadType.AllianceCancellation,
            WarDeclarationEventPayload => EventPayloadType.WarDeclaration,
            PeaceRequestEventPayload => EventPayloadType.PeaceRequest,
            ServerNotificationEventPayload => EventPayloadType.ServerNotification,
            _ => EventPayloadType.Unknown
        };
    }

    private static ProtocolIdentity ToProtocolIdentity(EventParty party)
    {
        return new ProtocolIdentity(party.UserId, party.ColonyId, snapshotId: null);
    }

    private static EventTargetContextDto? ToDto(EventTargetContext? context)
    {
        if (context is null)
        {
            return null;
        }

        return new EventTargetContextDto(
            context.WorldObjectId,
            context.MapUniqueId,
            context.Tile,
            context.LandingMode.ToString());
    }

    private static string PayloadSummary(LedgerEventPayload payload)
    {
        if (payload is SupportPawnEventPayload support)
        {
            string supportJson = JsonSerializer.Serialize(new
            {
                pawnGlobalKey = support.PawnGlobalKey,
                sourceSnapshotId = support.SourceSnapshotId,
                pawnName = support.PawnName,
                temporaryControl = support.TemporaryControl,
                expectedReturnAtUtc = support.ExpectedReturnAtUtc,
                pawnReference = support.PawnReference,
                pawnPackage = support.PawnPackage,
                sourceTile = support.SourceTile,
                sourceCaravanLoadId = support.SourceCaravanLoadId,
                returnToSender = support.ReturnToSender,
                rejectionReason = support.RejectionReason,
                permanentSupport = support.PermanentSupport,
                supportDurationDays = support.SupportDurationDays,
                expiresAtGameTicks = support.ExpiresAtGameTicks,
                autoReturnOnSettlement = support.AutoReturnOnSettlement,
                sourceEventId = support.SourceEventId,
                returnReason = support.ReturnReason
            });
            const int maxSupportLength = 2 * 1024 * 1024;
            return supportJson.Length <= maxSupportLength ? supportJson : supportJson.Substring(0, maxSupportLength);
        }

        if (payload is RaidEventPayload { AttackerLoss: not null } raidLoss)
        {
            return JsonSerializer.Serialize(new
            {
                raidLoss.AttackerLoss
            });
        }

        string json = JsonSerializer.Serialize(payload, payload.GetType());
        int maxLength = payload switch
        {
            ItemDeliveryEventPayload => 3 * 1024 * 1024,
            RaidEventPayload { Settlement: not null } => 2 * 1024 * 1024,
            _ => 4096
        };
        return json.Length <= maxLength ? json : json.Substring(0, maxLength);
    }

    private static ProtocolDeliverySemantics ToDeliverySemantics(EventQueueItem item)
    {
        if (!item.RequiresClientApplication)
        {
            return ProtocolDeliverySemantics.ServerNotification;
        }

        if (item.Status == ServerEventStatus.ReadyForImmediateDelivery)
        {
            return ProtocolDeliverySemantics.OnlineImmediate;
        }

        return item.Group switch
        {
            EventQueueGroupKind.DeliveredUnconfirmed => ProtocolDeliverySemantics.RequiresSnapshotConfirmation,
            EventQueueGroupKind.DirectlyProcessable => ProtocolDeliverySemantics.RequiresSnapshotConfirmation,
            EventQueueGroupKind.WaitingForConfirmation => ProtocolDeliverySemantics.OfflinePending,
            EventQueueGroupKind.Conflict => ProtocolDeliverySemantics.ServerNotification,
            EventQueueGroupKind.Rejected => ProtocolDeliverySemantics.ServerNotification,
            _ => ProtocolDeliverySemantics.OfflinePending
        };
    }

    private static SaveSnapshotIndex EmptyIndex(SnapshotIdentity identity)
    {
        return new SaveSnapshotIndex(
            "<network-payload>",
            new SaveMetaSummary(null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
            Array.Empty<FactionSummary>(),
            Array.Empty<SaveIndexExtensionData>(),
            Array.Empty<WorldObjectSummary>(),
            Array.Empty<MapSummary>(),
            Array.Empty<ThingSummary>(),
            Array.Empty<PawnSummary>());
    }
}
