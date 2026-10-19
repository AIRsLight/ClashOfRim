using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.World;
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
    private static ClientWorldObjectCommands? commands;
    internal static void AttachCommands(ClientWorldObjectCommands? value) { commands?.Reset(); commands = value; }

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

        var selection = commands?.Capture(new ClientWorldObjectCommandTarget(view));
        if (selection is null) yield break;
        var captured = selection.CopyTarget();
        if (selection.ObservationMode is { } mode)
        {
            bool blocked = selection.ObservationBlocked;
            string commandKey = mode == ClientRemoteMapModes.RaidObservation ? "ObserveRaid"
                : mode == ClientRemoteMapModes.FriendlyObservation ? "ObserveAlly" : "Scout";
            Command_Action command = new()
            {
                defaultLabel = ClashOfRimText.Key("ClashOfRim.RemoteWorldObject." + commandKey),
                defaultDesc = ClashOfRimText.Key("ClashOfRim.RemoteWorldObject." + commandKey + "Desc"),
                icon = RemoteWorldObjectCommandIcons.ShowMap,
                action = () => _ = selection.Observe()
            };
            if (blocked) command.Disable(ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.ActiveSessionBlocksOpen"));
            yield return command;
        }

        if (view.WorldObject is MapParent { HasMap: true } mapParent)
        {
            var capturedMap = mapParent.Map;
            yield return new Command_Action
            {
                defaultLabel = ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.Close"),
                defaultDesc = ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.CloseDesc"),
                icon = TexButton.CloseXSmall,
                action = () => selection.TryCommit(() => ReferenceEquals(capturedMap, mapParent.Map), () => RemoteSessionMapUtility.CloseObservationMap(mapParent))
            };
        }
        else if (useRuntimeActionNotice && (captured.CanRaid || captured.CanReinforce))
        {
            bool raidDisabled = captured.CanRaid && ClientTransporterActions.Current?.Capabilities.PvpEnabled != true;
            yield return new Command_Action
            {
                defaultLabel = ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.ActionNotice"),
                defaultDesc = raidDisabled
                    ? ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled")
                    : captured.CanRaid
                    ? ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.RaidActionNoticeDesc")
                    : ClashOfRimText.Key("ClashOfRim.RemoteWorldObject.ReinforceActionNoticeDesc"),
                icon = captured.CanRaid ? RemoteWorldObjectCommandIcons.LaunchRaid : RemoteWorldObjectCommandIcons.SendSupport,
                action = () => selection.TryStep(() =>
                {
                    bool disabled = captured.CanRaid && ClientTransporterActions.Current?.Capabilities.PvpEnabled != true;
                    string message = ClashOfRimText.Key(disabled ? "ClashOfRim.Raid.PvpDisabled" : captured.CanRaid
                        ? "ClashOfRim.RemoteWorldObject.RaidActionNoticeMessage" : "ClashOfRim.RemoteWorldObject.ReinforceActionNoticeMessage");
                    selection.TryStep(() => Messages.Message(message, view.WorldObject,
                        disabled ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.NeutralEvent, historical: false));
                })
            };
        }
    }

    public static IEnumerable<FloatMenuOption> BuildTransportersFloatMenuOptions(
        IRemoteWorldObjectView view, IEnumerable<IThingHolder> pods, Action<PlanetTile, TransportersArrivalAction> launchAction)
    {
        var selection = commands?.Capture(new ClientWorldObjectCommandTarget(view));
        var actions = ClientTransporterActions.Current;
        if (selection is null || actions is null) yield break;
        var target = selection.CopyTarget();
        string label = view.Label;
        PlanetTile destination = view.Tile;
        IReadOnlyList<IThingHolder> cargo = pods as IReadOnlyList<IThingHolder> ?? pods.ToList();
        bool containsNonGiftPawn = GiftTransporterPayloadUtility.ContainsNonGiftPawn(cargo);
        if (actions.CanOffer(target, ClientTransporterChoice.Raid))
            foreach (var option in TransportOptions(selection, actions, target, label, destination, cargo, launchAction,
                ClientTransporterChoice.Raid, () => new RemoteRaidTransportersArrivalAction(target, label),
                "ClashOfRim.Raid.TransportPodOption", "ClashOfRim.Raid.TransportPodConfirm")) yield return option;

        if (containsNonGiftPawn)
        {
            if (target.TileLayerId > 0 && target.RelationKind != "Hostile")
                yield return new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.Transporters.OrbitalNonHostileDisabled", label.Named("TARGET")), null);
            yield break;
        }
        if (actions.CanOffer(target, ClientTransporterChoice.Gift))
            foreach (var option in TransportOptions(selection, actions, target, label, destination, cargo, launchAction,
                ClientTransporterChoice.Gift, () => new RemoteGiftTransportersArrivalAction(target, label, false),
                "ClashOfRim.GiftDelivery.TransportPodGiftOption", null)) yield return option;
        if (actions.CanOffer(target, ClientTransporterChoice.ForcedDelivery))
            foreach (var option in TransportOptions(selection, actions, target, label, destination, cargo, launchAction,
                ClientTransporterChoice.ForcedDelivery, () => new RemoteGiftTransportersArrivalAction(target, label, true),
                "ClashOfRim.GiftDelivery.TransportPodForcedOption", "ClashOfRim.GiftDelivery.TransportPodForcedConfirm")) yield return option;
    }

    private static IEnumerable<FloatMenuOption> TransportOptions<T>(ClientWorldObjectCommands.Selection selection,
        ClientTransporterActions actions, ModWorldMapMarkerDto target, string label, PlanetTile destination,
        IReadOnlyList<IThingHolder> cargo, Action<PlanetTile, TransportersArrivalAction> launch, ClientTransporterChoice choice,
        Func<T> createArrival, string labelKey, string? confirmKey) where T : TransportersArrivalAction
    {
        FloatMenuAcceptanceReport Acceptance()
            => selection.IsCurrent && ReferenceEquals(actions, ClientTransporterActions.Current)
                ? actions.Acceptance(target, cargo, choice)
                : ClientTransporterActions.Reject(choice == ClientTransporterChoice.Raid
                    ? "ClashOfRim.Raid.StatusTargetIncomplete" : "ClashOfRim.GiftDelivery.StatusTargetIncomplete");
        return TransportersArrivalActionUtility.GetFloatMenuOptions(Acceptance, createArrival,
            ClashOfRimText.Key(labelKey, label.Named("TARGET")),
            (tile, arrival) => selection.TryCommit(() =>
            {
                var report = Acceptance();
                if (!report.Accepted) return false;
                // Compatibility cargo validation may have changed configuration.
                // Recheck only policy here, without scanning cargo a second time.
                return ReferenceEquals(actions, ClientTransporterActions.Current)
                    && actions.TargetRejection(target, choice) is null;
            }, () => launch(tile, arrival)), destination,
            confirmKey is null ? null : action => selection.TryPresent(() =>
            {
                string prompt = ClashOfRimText.Key(confirmKey, label.Named("TARGET"));
                selection.TryStep(() => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(prompt, action)));
            }));
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

    internal static ModWorldMapMarkerDto CaptureTarget(IRemoteWorldObjectView view)
    {
        return new ModWorldMapMarkerDto
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
            CanReinforce = view.CanReinforce,
            RelationKind = view.RelationKind
        };
    }

    internal static int ReadTileLayerId(PlanetTile tile)
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

}
