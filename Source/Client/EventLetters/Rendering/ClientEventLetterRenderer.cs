using static AIRsLight.ClashOfRim.Modules.Maps.ClientMapIdentity;
using static AIRsLight.ClashOfRim.Diplomacy.ClientDiplomacyPresentation;
using static AIRsLight.ClashOfRim.Modules.Events.ClientEventDiplomacyPolicy;
using static AIRsLight.ClashOfRim.EventLetters.ClientEventTextFormatting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.EventLetters.ClientEventLetterPolicy;

namespace AIRsLight.ClashOfRim.EventLetters;

internal static class ClientEventLetterRenderer
{
    internal static ClashOfRimEventLetterProjection BuildEventLetterProjection(ModEventDetailDto detail, string group)
    {
        LookTargets? lookTargets = ShouldShowLetterLocation(detail)
            ? BuildEventLookTargets(detail)
            : null;

        return new ClashOfRimEventLetterProjection(
            detail.EventId,
            BuildEventLetterLabel(detail),
            BuildEventLetterText(detail, group),
            ResolveLetterDef(detail),
            lookTargets,
            BuildEventLetterActions(detail, lookTargets?.IsValid == true, group));
    }

    private static string BuildEventLetterLabel(ModEventDetailDto detail)
    {
        return detail.EventType switch
        {
            ServerEventType.Raid when IsRaidAttackerLossDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.RaidAttackerLoss"),
            ServerEventType.Raid when IsRaidSettlementDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.RaidSettlement"),
            ServerEventType.Raid => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.Raid"),
            ServerEventType.ItemDelivery when IsForcedGiftDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.ForcedGift"),
            ServerEventType.ItemDelivery when IsTradeDeliveryDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.TradeDelivery"),
            ServerEventType.ItemDelivery when IsTradeReturnDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.TradeReturn"),
            ServerEventType.ItemDelivery when IsRejectedGiftReturnDetail(detail) => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.GiftReturn"),
            ServerEventType.ItemDelivery => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.Gift"),
            ServerEventType.Trade => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.Trade"),
            ServerEventType.SupportPawn => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.SupportPawn"),
            ServerEventType.AllianceRequest => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.AllianceRequest"),
            ServerEventType.AllianceCancellation => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.AllianceCancellation"),
            ServerEventType.WarDeclaration => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.WarDeclaration"),
            ServerEventType.PeaceRequest => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.PeaceRequest"),
            ServerEventType.ServerNotification => BuildServerNotificationEventLetterLabel(detail),
            _ => ClashOfRimText.Key("ClashOfRim.EventLetter.Label.ServerEvent")
        };
    }

    private static string BuildEventLetterText(ModEventDetailDto detail, string group)
    {
        if (detail.EventType == ServerEventType.Raid
            && TryBuildRaidSettlementEventLetterText(detail, out string raidSettlementText))
        {
            return raidSettlementText;
        }

        if (detail.EventType == ServerEventType.Raid
            && TryBuildRaidAttackerLossEventLetterText(detail, out string raidAttackerLossText))
        {
            return raidAttackerLossText;
        }

        if (IsReadOnlyEventGroup(group) && detail.EventType != ServerEventType.ServerNotification)
        {
            return BuildReadOnlyEventLetterText(detail, group);
        }

        if (ItemDeliveryClientProcessor.IsGiftDetail(detail))
        {
            return BuildGiftEventLetterText(detail);
        }

        if (IsDiplomacyEvent(detail))
        {
            return BuildDiplomacyEventLetterText(detail);
        }

        if (detail.EventType == ServerEventType.SupportPawn)
        {
            return BuildSupportPawnEventLetterText(detail, group);
        }

        if (detail.EventType == ServerEventType.ServerNotification)
        {
            return BuildServerNotificationEventLetterText(detail);
        }

        string actionText = BuildEventActionText(detail, group);
        if (!Prefs.DevMode)
        {
            return actionText;
        }

        string targetText = ShouldShowLetterLocation(detail)
            ? BuildTargetText(detail)
            : "";
        string payloadText = string.IsNullOrWhiteSpace(detail.PayloadSummary)
            ? ""
            : "\n" + ClashOfRimText.Key("ClashOfRim.EventLetter.PayloadSummary", Preview(detail.PayloadSummary, 900).Named("PAYLOAD"));

        return actionText
            + targetText
            + "\n\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.DebugHeading")
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.ServerEventLine", detail.EventId.Named("EVENTID"))
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.TypeLine", FormatEventType(detail.EventType).Named("TYPE"))
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.StatusLine", detail.Status.Named("STATUS"))
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.GroupLine", FormatEventGroupName(group).Named("GROUP"))
            + payloadText;
    }

    private static string BuildGiftEventLetterText(ModEventDetailDto detail)
    {
        string sender = FormatEventParty(detail.Actor);
        ItemDeliveryPayloadSummary? payload = ReadGiftPayloadForDisplay(detail);
        bool tradeDelivery = payload?.IsTradeDelivery == true;
        bool tradeReturn = payload?.IsTradeReturn == true;
        bool forcedGift = payload?.IsForcedDelivery == true;
        string intro = forcedGift
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.ForcedGiftIntro", sender.Named("SENDER"))
            : tradeDelivery
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.TradeDeliveryIntro", sender.Named("SENDER"))
            : tradeReturn
                ? ClashOfRimText.Key("ClashOfRim.EventLetter.TradeReturnIntro")
                : IsRejectedGiftReturnDetail(detail)
                    ? ClashOfRimText.Key("ClashOfRim.EventLetter.GiftReturnIntro", sender.Named("SENDER"))
                    : ClashOfRimText.Key("ClashOfRim.EventLetter.GiftIntro", sender.Named("SENDER"));
        string action = forcedGift
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.ForcedGiftAction")
            : tradeDelivery || tradeReturn
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.TradeDeliveryAction")
            : IsRejectedGiftReturnDetail(detail)
                ? ClashOfRimText.Key("ClashOfRim.EventLetter.GiftReturnAction")
                : ClashOfRimText.Key("ClashOfRim.EventLetter.GiftAction");
        string itemList = payload is null
            ? "- " + ClashOfRimText.Key("ClashOfRim.EventLetter.GiftItemsUnavailable")
            : BuildGiftItemListText(payload);

        return intro
            + "\n\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.GiftItemsHeading")
            + "\n"
            + itemList
            + "\n\n"
            + action;
    }

    private static string BuildDiplomacyEventLetterText(ModEventDetailDto detail)
    {
        string sender = FormatEventParty(detail.Actor);
        string intro = detail.EventType switch
        {
            ServerEventType.AllianceRequest => ClashOfRimText.Key("ClashOfRim.EventLetter.AllianceRequestIntro", sender.Named("SENDER")),
            ServerEventType.AllianceCancellation => ClashOfRimText.Key("ClashOfRim.EventLetter.AllianceCancellationIntro", sender.Named("SENDER")),
            ServerEventType.WarDeclaration => ClashOfRimText.Key("ClashOfRim.EventLetter.WarDeclarationIntro", sender.Named("SENDER")),
            ServerEventType.PeaceRequest => ClashOfRimText.Key("ClashOfRim.EventLetter.PeaceRequestIntro", sender.Named("SENDER")),
            _ => ClashOfRimText.Key("ClashOfRim.EventLetter.GenericAction")
        };
        string action = detail.EventType switch
        {
            ServerEventType.AllianceRequest => ClashOfRimText.Key("ClashOfRim.EventLetter.AllianceRequestAction"),
            ServerEventType.AllianceCancellation => ClashOfRimText.Key("ClashOfRim.EventLetter.AllianceCancellationAction"),
            ServerEventType.WarDeclaration => ClashOfRimText.Key("ClashOfRim.EventLetter.WarDeclarationAction"),
            ServerEventType.PeaceRequest => ClashOfRimText.Key("ClashOfRim.EventLetter.PeaceRequestAction"),
            _ => ClashOfRimText.Key("ClashOfRim.EventLetter.GenericAction")
        };
        string relation = ClashOfRimText.Key(
            "ClashOfRim.EventLetter.DiplomacyRelation",
            FormatDiplomacyRelationKind(ResolveDiplomacyRelationKind(detail.EventType)).Named("RELATION"));
        return intro + "\n\n" + relation + "\n\n" + action;
    }

    private static string BuildSupportPawnEventLetterText(ModEventDetailDto detail, string group)
    {
        string sender = FormatEventParty(detail.Actor);
        SupportPawnPayloadSummary? payload = ReadSupportPawnPayloadForDisplay(detail);
        string pawn = ResolveSupportPawnLabel(payload);
        string duration = FormatSupportDuration(payload);

        if (payload?.ReturnToSender == true)
        {
            string reason = string.IsNullOrWhiteSpace(payload.ReturnReason)
                ? ClashOfRimText.Key("ClashOfRim.EventLetter.SupportReturnReasonDefault")
                : payload.ReturnReason!;
            return ClashOfRimText.Key(
                "ClashOfRim.EventLetter.SupportReturnIntro",
                pawn.Named("PAWN"),
                reason.Named("REASON"));
        }

        if (string.Equals(group, "Rejected", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key(
                "ClashOfRim.EventLetter.SupportRejectedIntro",
                pawn.Named("PAWN"),
                sender.Named("SENDER"));
        }

        string intro = ClashOfRimText.Key(
            "ClashOfRim.EventLetter.SupportRequestIntro",
            sender.Named("SENDER"),
            pawn.Named("PAWN"));
        string term = ClashOfRimText.Key(
            "ClashOfRim.EventLetter.SupportTerm",
            duration.Named("DURATION"));
        string action = payload?.PermanentSupport == true
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.SupportPermanentRequestAction")
            : payload?.TemporaryControl == true
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.SupportRequestAction")
            : ClashOfRimText.Key("ClashOfRim.EventLetter.SupportNoticeAction");
        return intro + "\n\n" + term + "\n\n" + action;
    }

    private static string BuildServerNotificationEventLetterText(ModEventDetailDto detail)
    {
        ServerNotificationPayloadSummary? payload = ReadServerNotificationPayloadForDisplay(detail);
        if (payload is null)
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.ServerNotificationFallback");
        }

        string title = string.IsNullOrWhiteSpace(payload.Title)
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.Label.ServerNotification")
            : payload.Title!;
        string message = string.IsNullOrWhiteSpace(payload.Message)
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.ServerNotificationFallback")
            : payload.Message!;
        return title + "\n\n" + message;
    }

    private static bool TryBuildRaidSettlementEventLetterText(ModEventDetailDto detail, out string text)
    {
        text = string.Empty;
        RaidSettlementPayloadSummary? payload = ReadRaidSettlementPayloadForDisplay(detail);
        if (payload?.Settlement?.Losses is null)
        {
            return false;
        }

        IReadOnlyList<string> lossLines = BuildRaidDefenderLossLines(payload.Settlement.Losses);
        string outcome = lossLines.Count == 0
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.RaidNoDefenderLoss")
            : ClashOfRimText.Key("ClashOfRim.EventLetter.RaidSettlementIntro")
                + "\n\n"
                + ClashOfRimText.Key("ClashOfRim.EventLetter.RaidDefenderLossHeading")
                + "\n"
                + string.Join("\n", lossLines);
        text = ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLine", FormatEventParty(detail.Actor).Named("ATTACKER"))
            + "\n\n" + outcome;
        return true;
    }

    private static bool TryBuildRaidAttackerLossEventLetterText(ModEventDetailDto detail, out string text)
    {
        text = string.Empty;
        if (!RaidAttackerLossPayloadReader.TryReadSummary(detail, out RaidAttackerLossSummary? loss)
            || loss is null)
        {
            return false;
        }

        IReadOnlyList<string> pawnLines = BuildRaidAttackerLossPawnLines(loss);
        IReadOnlyList<string> thingLines = BuildRaidAttackerLossThingLines(loss.LostThings);
        text = ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLossIntro")
            + "\n\n"
            + string.Join("\n", pawnLines)
            + "\n\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLossThingsHeading")
            + "\n"
            + string.Join("\n", thingLines);
        return true;
    }

    private static IReadOnlyList<string> BuildRaidAttackerLossPawnLines(RaidAttackerLossSummary loss)
    {
        List<string> keys = (loss.LostPawnGlobalKeys ?? new List<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0)
        {
            return new[] { "- " + ClashOfRimText.Key("ClashOfRim.None") };
        }

        List<string> labels = new();
        int missing = 0;
        foreach (string key in keys)
        {
            if (TryFindLocalPawnByGlobalKey(key, out Pawn? pawn) && pawn is not null)
            {
                labels.Add(FormatRaidAttackerLostPawnLabel(pawn));
                continue;
            }

            missing++;
        }

        if (missing == keys.Count)
        {
            labels.Add(ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLossPawnCountFallback", keys.Count.Named("COUNT")));
        }
        else if (missing > 0)
        {
            labels.Add(ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLossPawnCountFallback", missing.Named("COUNT")));
        }

        return labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .OrderBy(label => label, StringComparer.Ordinal)
            .Select(label => "- " + ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAttackerLossPawnLine", label.Named("PAWN")))
            .ToList();
    }

    private static string FormatRaidAttackerLostPawnLabel(Pawn pawn)
    {
        string label = pawn.Name is null
            ? pawn.LabelShortCap
            : pawn.Name.ToStringShort;
        label = string.IsNullOrWhiteSpace(label)
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.SupportUnknownPawn")
            : label.Trim();
        string? title = pawn.story?.TitleShortCap;
        return string.IsNullOrWhiteSpace(title)
            ? label
            : ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RaidAttackerLossPawnWithTitle",
                label.Named("PAWN"),
                title.Named("TITLE"));
    }

    private static bool TryFindLocalPawnByGlobalKey(string globalKey, out Pawn? pawn)
    {
        pawn = null;
        HashSet<string> localKeys = BuildRaidAttackerLossLocalPawnKeys(globalKey);
        return localKeys.Count > 0
            && TryFindLocalPawnByKeys(localKeys, out pawn);
    }

    private static bool TryFindLocalPawnByKeys(HashSet<string> localKeys, out Pawn? pawn)
    {
        pawn = null;
        if (Current.ProgramState == ProgramState.Entry)
        {
            return false;
        }

        foreach (Pawn candidate in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
        {
            if (candidate is null || candidate.Destroyed)
            {
                continue;
            }

            if (!localKeys.Contains(candidate.ThingID) && !localKeys.Contains(candidate.GetUniqueLoadID()))
            {
                continue;
            }

            pawn = candidate;
            return true;
        }

        return false;
    }

    private static HashSet<string> BuildRaidAttackerLossLocalPawnKeys(string? globalKey)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        AddRaidAttackerLossLocalPawnKey(keys, globalKey);
        if (string.IsNullOrWhiteSpace(globalKey))
        {
            return keys;
        }

        string key = globalKey!;
        int thingMarker = key.LastIndexOf("/thing:", StringComparison.Ordinal);
        if (thingMarker >= 0)
        {
            AddRaidAttackerLossLocalPawnKey(keys, key.Substring(thingMarker + "/thing:".Length));
            return keys;
        }

        int looseMarker = key.LastIndexOf("thing:", StringComparison.Ordinal);
        if (looseMarker >= 0)
        {
            AddRaidAttackerLossLocalPawnKey(keys, key.Substring(looseMarker + "thing:".Length));
        }

        return keys;
    }

    private static void AddRaidAttackerLossLocalPawnKey(HashSet<string> keys, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        string trimmed = key!.Trim();
        keys.Add(trimmed);
        if (trimmed.StartsWith("Thing_", StringComparison.Ordinal))
        {
            keys.Add(trimmed.Substring("Thing_".Length));
        }
        else
        {
            keys.Add("Thing_" + trimmed);
        }
    }

    private static IReadOnlyList<string> BuildRaidAttackerLossThingLines(IReadOnlyList<RaidLostThingSummary>? things)
    {
        if (things is null || things.Count == 0)
        {
            return new[] { "- " + ClashOfRimText.Key("ClashOfRim.None") };
        }

        Dictionary<string, int> countsByLabel = new(StringComparer.Ordinal);
        foreach (RaidLostThingSummary thing in things)
        {
            string label = RaidLostThingLabel(thing);
            int count = Math.Max(1, thing.StackCount);
            countsByLabel[label] = countsByLabel.TryGetValue(label, out int existing)
                ? existing + count
                : count;
        }

        return countsByLabel
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => "- " + ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RaidAttackerLossThingLine",
                pair.Key.Named("THING"),
                pair.Value.Named("COUNT")))
            .ToList();
    }

    private static string RaidLostThingLabel(RaidLostThingSummary thing)
    {
        if (!string.IsNullOrWhiteSpace(thing.DisplayLabel))
        {
            return thing.DisplayLabel!;
        }

        if (!string.IsNullOrWhiteSpace(thing.Def))
        {
            ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(thing.Def);
            return def?.LabelCap.ToString() ?? thing.Def!;
        }

        return ClashOfRimText.Key("ClashOfRim.Unknown");
    }

    private static IReadOnlyList<string> BuildRaidDefenderLossLines(IReadOnlyList<RaidSettlementLossPayloadSummary> losses)
    {
        Dictionary<string, int> itemCountsByLabel = new(StringComparer.Ordinal);
        Dictionary<string, int> destroyedBuildingsByLabel = new(StringComparer.Ordinal);
        Dictionary<string, int> damagedBuildingsByLabel = new(StringComparer.Ordinal);
        foreach (RaidSettlementLossPayloadSummary loss in losses)
        {
            if (string.IsNullOrWhiteSpace(loss.Def))
            {
                continue;
            }

            ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(loss.Def);
            string label = def?.LabelCap.ToString() ?? loss.Def!;
            bool building = def?.category == ThingCategory.Building;
            if (building)
            {
                if (loss.LossCount > 0 && loss.WholeThingMissing)
                {
                    destroyedBuildingsByLabel[label] = destroyedBuildingsByLabel.TryGetValue(label, out int current)
                        ? current + Math.Max(1, loss.LossCount)
                        : Math.Max(1, loss.LossCount);
                }
                else if (loss.LossCount <= 0 && loss.RemainingHitPointsAfterDamage.HasValue)
                {
                    damagedBuildingsByLabel[label] = damagedBuildingsByLabel.TryGetValue(label, out int current)
                        ? current + 1
                        : 1;
                }

                continue;
            }

            if (loss.LossCount <= 0)
            {
                continue;
            }

            itemCountsByLabel[label] = itemCountsByLabel.TryGetValue(label, out int existing)
                ? existing + loss.LossCount
                : loss.LossCount;
        }

        var lines = new List<string>();
        lines.AddRange(itemCountsByLabel
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => "- " + ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RaidLossItemLine",
                pair.Key.Named("THING"),
                pair.Value.Named("COUNT"))));
        lines.AddRange(destroyedBuildingsByLabel
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => "- " + ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RaidLossBuildingLine",
                pair.Key.Named("THING"),
                pair.Value.Named("COUNT"))));
        lines.AddRange(damagedBuildingsByLabel
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => "- " + ClashOfRimText.Key(
                "ClashOfRim.EventLetter.RaidDamagedBuildingLine",
                pair.Key.Named("THING"),
                pair.Value.Named("COUNT"))));
        return lines;
    }

    private static RaidSettlementPayloadSummary? ReadRaidSettlementPayloadForDisplay(ModEventDetailDto detail)
    {
        if (string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            return null;
        }

        try
        {
            return RaidSettlementPayloadReader.Read(detail.PayloadSummary);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            Log.Warning(
                $"[ClashOfRim][EventLetter] raid settlement payload display parse failed event={detail.EventId} payloadLength={detail.PayloadSummary.Length} exception={ex}");
            return null;
        }
    }

    private static string BuildServerNotificationEventLetterLabel(ModEventDetailDto detail)
    {
        ServerNotificationPayloadSummary? payload = ReadServerNotificationPayloadForDisplay(detail);
        string? title = payload?.Title;
        return string.IsNullOrWhiteSpace(title)
            ? ClashOfRimText.Key("ClashOfRim.EventLetter.Label.ServerNotification")
            : title!;
    }

    private static bool IsReadOnlyEventGroup(string group)
    {
        return string.Equals(group, "Rejected", StringComparison.Ordinal)
            || string.Equals(group, "DeliveredUnconfirmed", StringComparison.Ordinal)
            || string.Equals(group, "Conflicts", StringComparison.Ordinal);
    }

    private static string BuildReadOnlyEventLetterText(ModEventDetailDto detail, string group)
    {
        string status = group switch
        {
            "Rejected" => ClashOfRimText.Key("ClashOfRim.EventLetter.ReadOnlyRejected"),
            "DeliveredUnconfirmed" => ClashOfRimText.Key("ClashOfRim.EventLetter.ReadOnlyDeliveredUnconfirmed"),
            "Conflicts" => ClashOfRimText.Key("ClashOfRim.EventLetter.ReadOnlyConflict"),
            _ => ClashOfRimText.Key("ClashOfRim.EventLetter.GenericAction")
        };
        string type = ClashOfRimText.Key(
            "ClashOfRim.EventLetter.ReadOnlyType",
            FormatEventType(detail.EventType).Named("TYPE"));
        string targetText = Prefs.DevMode && ShouldShowLetterLocation(detail)
            ? BuildTargetText(detail)
            : "";
        string devText = !Prefs.DevMode
            ? ""
            : "\n\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.DebugHeading")
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.ServerEventLine", detail.EventId.Named("EVENTID"))
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.StatusLine", detail.Status.Named("STATUS"))
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.EventLetter.GroupLine", FormatEventGroupName(group).Named("GROUP"));

        return status + "\n\n" + type + targetText + devText;
    }

    private static SupportPawnPayloadSummary? ReadSupportPawnPayloadForDisplay(ModEventDetailDto detail)
    {
        if (string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            return null;
        }

        try
        {
            return SupportPawnPayloadReader.Read(detail.PayloadSummary);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            Log.Warning(
                $"[ClashOfRim][EventLetter] support pawn payload display parse failed event={detail.EventId} payloadLength={detail.PayloadSummary.Length} exception={ex}");
            return null;
        }
    }

    private static string BuildGiftItemListText(ItemDeliveryPayloadSummary payload)
    {
        if (payload.Items.Count == 0)
        {
            return "- " + ClashOfRimText.Key("ClashOfRim.EventLetter.GiftItemsEmpty");
        }

        return string.Join("\n", payload.Items.Select(item => "- " + FormatGiftItem(item)));
    }

    private static string FormatGiftItem(ItemDeliveryItemSummary item)
    {
        ThingDef? def = TradeUiUtility.ResolveThingDef(item.MinifiedInnerDefName)
            ?? TradeUiUtility.ResolveThingDef(item.Def);
        string label = !string.IsNullOrWhiteSpace(item.DisplayLabel)
            ? item.DisplayLabel!
            : def?.label?.CapitalizeFirst() ?? item.Def ?? ClashOfRimText.Key("ClashOfRim.UnknownItem");
        int stackCount = Math.Max(1, item.StackCount);
        string countText = stackCount > 1 ? " x" + stackCount.ToString(CultureInfo.InvariantCulture) : string.Empty;
        string condition = FormatGiftItemCondition(item, def);
        return label + countText + condition;
    }

    private static string FormatGiftItemCondition(ItemDeliveryItemSummary item, ThingDef? def)
    {
        List<string> parts = new();
        string? quality = item.MinifiedInnerQuality ?? item.Quality;
        if (!string.IsNullOrWhiteSpace(quality))
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.ItemQuality", quality.Named("QUALITY")));
        }

        int? hitPoints = item.MinifiedInnerHitPoints ?? item.HitPoints;
        if (hitPoints.HasValue)
        {
            parts.Add(TradeUiUtility.FormatHitPointsPercent(hitPoints.Value, def));
        }

        if (item.WornByCorpse == true)
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.ItemWornByCorpse"));
        }

        if (item.Biocoded == true)
        {
            parts.Add(string.IsNullOrWhiteSpace(item.BiocodedPawnLabel)
                ? ClashOfRimText.Key("ClashOfRim.ItemBiocoded")
                : ClashOfRimText.Key("ClashOfRim.ItemBiocodedTo", item.BiocodedPawnLabel.Named("PAWN")));
        }

        if (item.UniqueWeapon == true)
        {
            parts.Add(ClashOfRimText.Key(WeaponTraitLabelKey(item.Metadata, def)));
            string[] traits = item.UniqueWeaponTraits
                .Where(trait => !string.IsNullOrWhiteSpace(trait))
                .ToArray();
            if (traits.Length > 0)
            {
                parts.Add(ClashOfRimText.Key("ClashOfRim.ItemUniqueWeaponTraits", string.Join("/", traits).Named("TRAITS")));
            }
        }

        return parts.Count == 0
            ? string.Empty
            : " (" + string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), parts) + ")";
    }

    private static string WeaponTraitLabelKey(IReadOnlyDictionary<string, string?>? metadata, ThingDef? def)
    {
        string? kind = null;
        if (metadata is not null
            && metadata.TryGetValue(TradeThingReferenceUtility.WeaponTraitKindMetadataKey, out string? value)
            && !string.IsNullOrWhiteSpace(value))
        {
            kind = value;
        }

        kind ??= TradeThingReferenceUtility.WeaponTraitKind(def);
        return kind switch
        {
            TradeThingReferenceUtility.WeaponTraitKindPersona => "ClashOfRim.ItemPersonaWeapon",
            TradeThingReferenceUtility.WeaponTraitKindSpecialized => "ClashOfRim.ItemSpecializedWeapon",
            _ => "ClashOfRim.ItemSpecializedWeapon"
        };
    }

    private static string FormatEventParty(ModProtocolIdentityDto? party)
    {
        if (party is null || string.IsNullOrWhiteSpace(party.UserId))
        {
            return ClashOfRimText.Key("ClashOfRim.UnknownOtherPlayer");
        }

        return string.IsNullOrWhiteSpace(party.ColonyId)
            ? party.UserId
            : party.UserId + "/" + party.ColonyId;
    }

    private static string ResolveSupportPawnLabel(SupportPawnPayloadSummary? payload)
    {
        if (!string.IsNullOrWhiteSpace(payload?.PawnName))
        {
            return payload!.PawnName!;
        }

        if (!string.IsNullOrWhiteSpace(payload?.PawnPackage?.Appearance?.DisplayName))
        {
            return payload!.PawnPackage!.Appearance!.DisplayName!;
        }

        return ClashOfRimText.Key("ClashOfRim.EventLetter.SupportUnknownPawn");
    }

    private static string FormatSupportDuration(SupportPawnPayloadSummary? payload)
    {
        if (payload?.PermanentSupport == true)
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.SupportDurationPermanent");
        }

        int days = Math.Max(1, payload?.SupportDurationDays ?? 0);
        return ClashOfRimText.Key(
            "ClashOfRim.EventLetter.SupportDurationDays",
            days.Named("DAYS"));
    }

    private static string BuildEventActionText(ModEventDetailDto detail, string group)
    {
        if (string.Equals(group, "DeliveredUnconfirmed", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.ReadOnlyDeliveredUnconfirmed");
        }

        if (detail.EventType == ServerEventType.ItemDelivery)
        {
            return IsRejectedGiftReturnDetail(detail)
                ? ClashOfRimText.Key("ClashOfRim.EventLetter.GiftReturnAction")
                : ClashOfRimText.Key("ClashOfRim.EventLetter.GiftAction");
        }

        if (detail.EventType == ServerEventType.Raid)
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.RaidAction");
        }

        if (detail.EventType == ServerEventType.Trade)
        {
            return ClashOfRimText.Key("ClashOfRim.EventLetter.TradeAction");
        }

        return ClashOfRimText.Key("ClashOfRim.EventLetter.GenericAction");
    }

    private static bool ShouldShowLetterLocation(ModEventDetailDto detail)
    {
        return detail.EventType != ServerEventType.ItemDelivery
            && detail.EventType != ServerEventType.SupportPawn;
    }

    private static string BuildTargetText(ModEventDetailDto detail)
    {
        List<string> parts = new();
        ModEventTargetContextDto? targetContext = detail.TargetContext;
        string? worldObjectId = targetContext?.WorldObjectId;
        if (!string.IsNullOrWhiteSpace(worldObjectId))
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.TargetWorldObject", worldObjectId.Named("ID")));
        }

        string? mapUniqueId = targetContext?.MapUniqueId;
        if (!string.IsNullOrWhiteSpace(mapUniqueId))
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.TargetMap", mapUniqueId.Named("ID")));
        }

        if (targetContext?.Tile is int tile)
        {
            parts.Add(ClashOfRimText.Key("ClashOfRim.TargetTile", tile.Named("TILE")));
        }

        return parts.Count == 0
            ? ""
            : "\n" + ClashOfRimText.Key("ClashOfRim.Target", string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), parts).Named("TARGET"));
    }

    private static LookTargets? BuildEventLookTargets(ModEventDetailDto detail)
    {
        if (IsRaidSettlementDetail(detail)
            && TryBuildRaidSettlementLookTargets(detail, out LookTargets? raidSettlementTargets))
        {
            return raidSettlementTargets;
        }

        Map? map = Find.CurrentMap;
        if (map is not null
            && MapIdsMatch($"Map_{map.uniqueID}", detail.TargetContext?.MapUniqueId))
        {
            return new LookTargets(map.Center, map);
        }

        return null;
    }

    private static bool TryBuildRaidSettlementLookTargets(ModEventDetailDto detail, out LookTargets? lookTargets)
    {
        lookTargets = null;
        Map? map = Find.CurrentMap;
        if (map is null)
        {
            return false;
        }

        RaidSettlementPayloadSummary? payload = ReadRaidSettlementPayloadForDisplay(detail);
        if (payload?.Settlement?.Losses is null || payload.Settlement.Losses.Count == 0)
        {
            return false;
        }

        var targets = new List<TargetInfo>();
        foreach (RaidSettlementLossPayloadSummary loss in payload.Settlement.Losses)
        {
            if (!MapIdsMatch($"Map_{map.uniqueID}", loss.MapUniqueId ?? detail.TargetContext?.MapUniqueId)
                || !TryParseLossCell(loss.Position, out IntVec3 cell)
                || !cell.InBounds(map))
            {
                continue;
            }

            targets.Add(new TargetInfo(cell, map));
            if (targets.Count >= 8)
            {
                break;
            }
        }

        if (targets.Count == 0)
        {
            return false;
        }

        lookTargets = new LookTargets(targets);
        return true;
    }

    private static bool TryParseLossCell(string? value, out IntVec3 cell)
    {
        cell = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value!.Trim().Trim('(', ')').Split(',');
        if (parts.Length < 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            || !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
        {
            return false;
        }

        cell = new IntVec3(x, y, z);
        return true;
    }

    private static string FormatEventType(ServerEventType eventType)
    {
        return eventType switch
        {
            ServerEventType.Raid => ClashOfRimText.Key("ClashOfRim.EventType.Raid"),
            ServerEventType.ItemDelivery => ClashOfRimText.Key("ClashOfRim.EventType.ItemDelivery"),
            ServerEventType.Trade => ClashOfRimText.Key("ClashOfRim.EventType.Trade"),
            ServerEventType.SupportPawn => ClashOfRimText.Key("ClashOfRim.EventType.SupportPawn"),
            ServerEventType.AllianceRequest => ClashOfRimText.Key("ClashOfRim.EventType.AllianceRequest"),
            ServerEventType.AllianceCancellation => ClashOfRimText.Key("ClashOfRim.EventType.AllianceCancellation"),
            ServerEventType.WarDeclaration => ClashOfRimText.Key("ClashOfRim.EventType.WarDeclaration"),
            ServerEventType.PeaceRequest => ClashOfRimText.Key("ClashOfRim.EventType.PeaceRequest"),
            ServerEventType.ServerNotification => ClashOfRimText.Key("ClashOfRim.EventType.ServerNotification"),
            _ => ClashOfRimText.Key("ClashOfRim.Unknown")
        };
    }
}
