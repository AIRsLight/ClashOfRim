using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal static class RemoteWorldObjectUiUtility
{
    public static string BuildInspectString(IRemoteWorldObjectView view, string baseInspectString)
    {
        var builder = new StringBuilder(baseInspectString);
        AppendLine(builder, ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.OwnerInspect", view.OwnerUserId.Named("OWNER")));
        AppendLine(builder, ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.RelationInspect", FormatRelationKind(view.RelationKind).Named("RELATION")));
        AppendLine(builder, ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.OnlineInspect", FormatOnlineStatus(view).Named("STATUS")));
        if (!view.OwnerOnline)
        {
            AppendLine(builder, ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.LastSeenInspect", FormatLastSeenAt(view.OwnerLastSeenAtUtc).Named("TIME")));
        }

        AppendLine(builder, ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.TileInspect", view.Tile.Named("TILE")));
        AppendRaidUnavailableLine(builder, view);
        if (Prefs.DevMode && !string.IsNullOrWhiteSpace(view.SourceWorldObjectId))
        {
            AppendLine(builder, ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.WorldObjectInspect", view.SourceWorldObjectId.Named("ID")));
        }

        if (Prefs.DevMode && !string.IsNullOrWhiteSpace(view.SourceMapId))
        {
            AppendLine(builder, ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.MapInspect", view.SourceMapId.Named("ID")));
        }

        if (Prefs.DevMode && !string.IsNullOrWhiteSpace(view.SourceSnapshotId))
        {
            AppendLine(builder, ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.SnapshotInspect", view.SourceSnapshotId.Named("ID")));
        }

        return builder.ToString();
    }

    public static IEnumerable<Gizmo> BuildGizmos(
        IRemoteWorldObjectView view,
        IEnumerable<Gizmo> baseGizmos,
        bool useRuntimeActionNotice)
    {
        foreach (Gizmo gizmo in baseGizmos)
        {
            yield return gizmo;
        }

        if (CanScout(view))
        {
            bool allied = IsAlliedWithPlayer(view);
            bool blocked = HasActiveRemoteMapSession();
            Command_Action command = new()
            {
                defaultLabel = ClashOfRimText.Key(allied
                    ? "ClashOfRim.RemoteWorldObject.ObserveAlly"
                    : "ClashOfRim.RemoteWorldObject.Scout"),
                defaultDesc = ClashOfRimText.Key(allied
                    ? "ClashOfRim.RemoteWorldObject.ObserveAllyDesc"
                    : "ClashOfRim.RemoteWorldObject.ScoutDesc"),
                icon = RemoteWorldObjectCommandIcons.ShowMap,
                action = () => StartObservation(view, allied
                    ? RemoteSessionMapParent.FriendlyObservationMode
                    : RemoteSessionMapParent.ScoutMode)
            };
            if (blocked)
            {
                command.Disable(ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.ActiveSessionBlocksOpen"));
            }

            yield return command;
        }

        if (CanObserveRaid(view))
        {
            bool blocked = HasActiveRemoteMapSession();
            Command_Action command = new()
            {
                defaultLabel = ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.ObserveRaid"),
                defaultDesc = ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.ObserveRaidDesc"),
                icon = RemoteWorldObjectCommandIcons.ShowMap,
                action = () => StartObservation(view, RemoteSessionMapParent.RaidObservationMode)
            };
            if (blocked)
            {
                command.Disable(ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.ActiveSessionBlocksOpen"));
            }

            yield return command;
        }

        if (view.WorldObject is MapParent { HasMap: true } mapParent)
        {
            yield return new Command_Action
            {
                defaultLabel = ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.Close"),
                defaultDesc = ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.CloseDesc"),
                icon = TexButton.CloseXSmall,
                action = () => RemoteSessionMapUtility.CloseObservationMap(mapParent)
            };
        }
        else if (useRuntimeActionNotice && (view.CanRaid || view.CanReinforce))
        {
            bool raidDisabled = view.CanRaid && LoadedModManager.GetMod<ClashOfRimMod>()?.PvpEnabled != true;
            yield return new Command_Action
            {
                defaultLabel = ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.ActionNotice"),
                defaultDesc = raidDisabled
                    ? ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled")
                    : view.CanRaid
                    ? ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.RaidActionNoticeDesc")
                    : ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.ReinforceActionNoticeDesc"),
                icon = view.CanRaid ? RemoteWorldObjectCommandIcons.LaunchRaid : RemoteWorldObjectCommandIcons.SendSupport,
                action = () => Messages.Message(
                    raidDisabled
                        ? ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled")
                        : view.CanRaid
                        ? ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.RaidActionNoticeMessage")
                        : ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.ReinforceActionNoticeMessage"),
                    view.WorldObject,
                    raidDisabled ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.NeutralEvent,
                    historical: false)
            };
        }
    }

    public static IEnumerable<FloatMenuOption> BuildTransportersFloatMenuOptions(
        IRemoteWorldObjectView view,
        IEnumerable<IThingHolder> pods,
        Action<PlanetTile, TransportersArrivalAction> launchAction)
    {
        IReadOnlyList<IThingHolder> podList = pods as IReadOnlyList<IThingHolder> ?? pods.ToList();
        bool containsNonGiftPawn = GiftTransporterPayloadUtility.ContainsNonGiftPawn(podList);
        if (CanReceiveTransportPodRaid(view))
        {
            string label = ClashOfRimText.Key(
                "ClashOfRim.Raid.TransportPodOption",
                view.Label.Named("TARGET"));
            foreach (FloatMenuOption option in TransportersArrivalActionUtility.GetFloatMenuOptions<RemoteRaidTransportersArrivalAction>(
                         () => CanSendTransportPodRaid(view, podList),
                         () => new RemoteRaidTransportersArrivalAction(view),
                         label,
                         launchAction,
                         view.Tile,
                         action => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                             ClashOfRimText.Key("ClashOfRim.Raid.TransportPodConfirm", view.Label.Named("TARGET")),
                             action))))
            {
                yield return option;
            }
        }

        if (containsNonGiftPawn && IsNonHostileOrbitalTarget(view))
        {
            yield return new FloatMenuOption(
                ClashOfRimText.Key("ClashOfRim.Transporters.OrbitalNonHostileDisabled", view.Label.Named("TARGET")),
                null);
            yield break;
        }

        if (containsNonGiftPawn)
        {
            yield break;
        }

        if (CanReceiveTransportPodDelivery(view, forced: false))
        {
            string label = ClashOfRimText.Key(
                "ClashOfRim.GiftDelivery.TransportPodGiftOption",
                view.Label.Named("TARGET"));
            foreach (FloatMenuOption option in TransportersArrivalActionUtility.GetFloatMenuOptions<RemoteGiftTransportersArrivalAction>(
                         () => CanSendTransportPodDelivery(view, podList, forced: false),
                         () => new RemoteGiftTransportersArrivalAction(view, forcedDelivery: false),
                         label,
                         launchAction,
                         view.Tile))
            {
                yield return option;
            }
        }

        if (CanReceiveTransportPodDelivery(view, forced: true))
        {
            string label = ClashOfRimText.Key(
                "ClashOfRim.GiftDelivery.TransportPodForcedOption",
                view.Label.Named("TARGET"));
            foreach (FloatMenuOption option in TransportersArrivalActionUtility.GetFloatMenuOptions<RemoteGiftTransportersArrivalAction>(
                         () => CanSendTransportPodDelivery(view, podList, forced: true),
                         () => new RemoteGiftTransportersArrivalAction(view, forcedDelivery: true),
                         label,
                         launchAction,
                         view.Tile,
                         action => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                             ClashOfRimText.Key("ClashOfRim.GiftDelivery.TransportPodForcedConfirm", view.Label.Named("TARGET")),
                             action))))
            {
                yield return option;
            }
        }
    }

    public static string BuildLabel(IRemoteWorldObjectView view, bool useRuntimeKindFallback)
    {
        string owner = string.IsNullOrWhiteSpace(view.OwnerUserId)
            ? ClashOfRimText.Key("ClashOfRim.UnknownPlayer")
            : view.OwnerUserId;
        if (!string.IsNullOrWhiteSpace(view.SourceLabel))
        {
            if (view.RuntimeKind is "TradeableColony" or "ActiveRaidTarget")
            {
                return view.SourceLabel;
            }

            return ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.NamedLabel", owner.Named("OWNER"), view.SourceLabel.Named("LABEL"));
        }

        if (!useRuntimeKindFallback)
        {
            return ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.ColonyLabel", owner.Named("OWNER"));
        }

        string key = view.RuntimeKind switch
        {
            "TradeableColony" => "ClashOfRim.RemoteWorldObject.ColonyLabel",
            "RuntimeShuttle" => "ClashOfRim.RemoteWorldObject.ShuttleLabel",
            "RuntimeTransportPod" => "ClashOfRim.RemoteWorldObject.TransportPodLabel",
            _ => "ClashOfRim.RemoteWorldObject.CaravanLabel"
        };
        return ClashOfRimText.Key(key, owner.Named("OWNER"));
    }

    private static void AppendLine(StringBuilder builder, string value)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(value);
    }

    private static bool CanReceiveTransportPodDelivery(IRemoteWorldObjectView view, bool forced)
    {
        if (!string.Equals(view.RuntimeKind, "TradeableColony", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(view.OwnerUserId)
            || string.IsNullOrWhiteSpace(view.OwnerColonyId)
            || string.IsNullOrWhiteSpace(view.SourceMapId))
        {
            return false;
        }

        if (forced && LoadedModManager.GetMod<ClashOfRimMod>()?.PvpEnabled != true)
        {
            return false;
        }

        return forced ? view.CanRaid : true;
    }

    private static bool CanReceiveTransportPodRaid(IRemoteWorldObjectView view)
    {
        return LoadedModManager.GetMod<ClashOfRimMod>()?.PvpEnabled == true
            && view.CanRaid
            && !string.IsNullOrWhiteSpace(view.OwnerUserId)
            && !string.IsNullOrWhiteSpace(view.OwnerColonyId)
            && !string.IsNullOrWhiteSpace(view.SourceMapId)
            && !string.IsNullOrWhiteSpace(view.SourceWorldObjectId);
    }

    private static FloatMenuAcceptanceReport CanSendTransportPodRaid(
        IRemoteWorldObjectView view,
        IReadOnlyList<IThingHolder> pods)
    {
        if (!CanReceiveTransportPodRaid(view))
        {
            string reason = LoadedModManager.GetMod<ClashOfRimMod>()?.PvpEnabled != true
                ? ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled")
                : ClashOfRimText.Key("ClashOfRim.Raid.StatusTargetIncomplete");
            return FloatMenuAcceptanceReport.WithFailReason(reason);
        }

        return pods.Count > 0 && TransportersArrivalActionUtility.AnyNonDownedColonist(pods)
            ? FloatMenuAcceptanceReport.WasAccepted
            : FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.Raid.TransportPodRejectNoColonist"));
    }

    private static FloatMenuAcceptanceReport CanSendTransportPodDelivery(
        IRemoteWorldObjectView view,
        IEnumerable<IThingHolder> pods,
        bool forced)
    {
        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        if (mod?.GiftsEnabled != true)
        {
            return FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.GiftDelivery.Disabled"));
        }

        if (forced && mod.PvpEnabled != true)
        {
            return FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled"));
        }

        if (!CanReceiveTransportPodDelivery(view, forced))
        {
            return FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusTargetIncomplete"));
        }

        return GiftTransporterPayloadUtility.CanSend(pods);
    }

    private static string FormatRelationKind(string? relationKind)
    {
        return relationKind switch
        {
            "Ally" => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationAlly"),
            "Hostile" => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationHostile"),
            _ => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationNeutral")
        };
    }

    private static string FormatOnlineStatus(IRemoteWorldObjectView view)
    {
        return view.OwnerOnline
            ? ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.StatusOnline")
            : ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.StatusOffline");
    }

    private static string FormatLastSeenAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.LastSeenUnknown");
        }

        if (!DateTimeOffset.TryParse(value, out DateTimeOffset utc))
        {
            return ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.LastSeenUnknown");
        }

        return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private static void AppendRaidUnavailableLine(StringBuilder builder, IRemoteWorldObjectView view)
    {
        if (view.CanRaid
            || !string.Equals(view.RelationKind, "Hostile", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(view.RaidUnavailableReason))
        {
            return;
        }

        string reason = FormatRaidUnavailableReason(view.RaidUnavailableReason);
        if (string.Equals(view.RaidUnavailableReason, "CooldownActive", StringComparison.Ordinal)
            && TryFormatLocalDateTime(view.RaidUnavailableUntilUtc, out string untilText))
        {
            AppendLine(builder, ClashOfRimText.Key(
                "ClashOfRim.RemoteWorldObject.RaidUnavailableUntilInspect",
                reason.Named("REASON"),
                untilText.Named("TIME")));
            return;
        }

        AppendLine(builder, ClashOfRimText.Key(
            "ClashOfRim.RemoteWorldObject.RaidUnavailableInspect",
            reason.Named("REASON")));
    }

    private static string FormatRaidUnavailableReason(string? reason)
    {
        return reason switch
        {
            "WealthBelowMinimum" => ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.RaidUnavailableReasonWealth"),
            "CooldownActive" => ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.RaidUnavailableReasonCooldown"),
            "DefenderOnline" => ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.RaidUnavailableReasonOnline"),
            _ => ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.RaidUnavailableReasonUnknown")
        };
    }

    private static bool TryFormatLocalDateTime(string? value, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !DateTimeOffset.TryParse(value, out DateTimeOffset utc))
        {
            return false;
        }

        text = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return true;
    }

    private static bool CanScout(IRemoteWorldObjectView view)
    {
        return string.Equals(view.RuntimeKind, "TradeableColony", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(view.OwnerUserId)
            && !string.IsNullOrWhiteSpace(view.OwnerColonyId);
    }

    private static bool CanObserveRaid(IRemoteWorldObjectView view)
    {
        return string.Equals(view.RuntimeKind, "ActiveRaidTarget", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(view.RelatedEventId)
            && !string.IsNullOrWhiteSpace(view.OwnerUserId)
            && !string.IsNullOrWhiteSpace(view.OwnerColonyId);
    }

    private static void StartObservation(IRemoteWorldObjectView view, string mode)
    {
        if (HasActiveRemoteMapSession())
        {
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.ActiveSessionBlocksOpen"),
                MessageTypeDefOf.RejectInput,
                historical: false);
            return;
        }

        var target = new ModWorldMapMarkerDto
        {
            MarkerId = view.MarkerId,
            Kind = view.RuntimeKind,
            OwnerUserId = view.OwnerUserId,
            OwnerColonyId = view.OwnerColonyId,
            WorldObjectId = view.SourceWorldObjectId,
            MapId = view.SourceMapId,
            SnapshotId = view.SourceSnapshotId,
            Tile = view.Tile,
            TileLayerId = ReadTileLayerId(view.Tile),
            Label = view.SourceLabel,
            RelatedEventId = view.RelatedEventId,
            CanRaid = view.CanRaid,
            CanTrade = view.CanTrade,
            CanReinforce = view.CanReinforce
        };
        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        mod.StartObserveRemoteMarker(target, mode);
    }

    private static bool HasActiveRemoteMapSession()
    {
        ActiveRemoteMapSession? session = ClashOfRimGameComponent.ActiveRemoteMapSession;
        if (session?.IsActive == true)
        {
            return true;
        }

        return RemoteSessionMapUtility.FindActiveSessionMap() is not null;
    }

    private static int ReadTileLayerId(PlanetTile tile)
    {
        try
        {
            return tile.Valid ? Math.Max(0, tile.Layer.LayerID) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsAlliedWithPlayer(IRemoteWorldObjectView view)
    {
        if (view.CanReinforce)
        {
            return true;
        }

        Faction? proxy = PlayerFactionProxyUtility.EnsureProxyForUser(view.OwnerUserId);
        return proxy is not null
            && Faction.OfPlayer is not null
            && proxy.RelationKindWith(Faction.OfPlayer) == FactionRelationKind.Ally;
    }

    private static bool IsNonHostileOrbitalTarget(IRemoteWorldObjectView view)
    {
        return ReadTileLayerId(view.Tile) > 0
            && !string.Equals(view.RelationKind, "Hostile", StringComparison.Ordinal);
    }
}
