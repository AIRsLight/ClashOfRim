using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Protocol;
using RimWorld;
using Verse;
using static AIRsLight.ClashOfRim.EventLetters.ClientEventLetterPolicy;

internal static class EventLetterPolicyTests
{
    public static void Run()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        var originalDefs = (LetterDefOf.PositiveEvent, LetterDefOf.NeutralEvent,
            LetterDefOf.NegativeEvent, LetterDefOf.ThreatBig);
        try
        {
            LetterDefOf.PositiveEvent = new LetterDef { defName = "PositiveEvent" };
            LetterDefOf.NeutralEvent = new LetterDef { defName = "NeutralEvent" };
            LetterDefOf.NegativeEvent = new LetterDef { defName = "NegativeEvent" };
            LetterDefOf.ThreatBig = new LetterDef { defName = "ThreatBig" };
            foreach (string language in new[] { "en-US", "zh-CN", "tr-TR", "ru-RU" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
                VerifyMatrix();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
            (LetterDefOf.PositiveEvent, LetterDefOf.NeutralEvent,
                LetterDefOf.NegativeEvent, LetterDefOf.ThreatBig) = originalDefs;
        }
    }

    private static void VerifyMatrix()
    {
        // Expected behavior describes the real game UI; no alternate projection is built here.
        Check(Detail(ServerEventType.ItemDelivery, "{\"Purpose\":0}"), true, true, true, "PositiveEvent");
        Check(Detail(ServerEventType.ItemDelivery, "{\"Purpose\":0,\"DeliveryKind\":\"Forced\"}"), true, false, true, "NegativeEvent");
        Check(Detail(ServerEventType.ItemDelivery, "{\"Purpose\":1}"), true, false, true, "NeutralEvent");
        foreach (int purpose in Enumerable.Range(2, 7))
        {
            ModEventDetailDto delivery = Detail(ServerEventType.ItemDelivery, "{\"Purpose\":" + purpose + "}");
            Check(delivery, true, false, true, "PositiveEvent");
            Require(IsTradeDeliveryDetail(delivery) == (purpose <= 3), "Completed trade classification");
            Require(IsTradeReturnDetail(delivery) == (purpose >= 4), "Refund classification");
        }

        ModEventDetailDto wrongPayload = Detail(ServerEventType.ItemDelivery, "{\"Purpose\":0}");
        wrongPayload.PayloadType = EventPayloadType.Unknown;
        Check(wrongPayload, false, false, false, "PositiveEvent");
        Check(Detail(ServerEventType.SupportPawn, "{\"temporaryControl\":true}"), true, true, false, "PositiveEvent");
        Check(Detail(ServerEventType.SupportPawn, "{\"permanentSupport\":true}"), true, false, false, "PositiveEvent");
        Check(Detail(ServerEventType.SupportPawn, "{\"temporaryControl\":true,\"returnToSender\":true}"), true, false, true, "PositiveEvent");
        Check(Detail(ServerEventType.SupportPawn, ""), false, false, false, "PositiveEvent");
        Check(Detail(ServerEventType.AllianceRequest), true, true, false, "NeutralEvent");
        Check(Detail(ServerEventType.PeaceRequest), true, true, false, "NeutralEvent");
        Check(Detail(ServerEventType.AllianceCancellation), false, false, false, "NeutralEvent");
        Check(Detail(ServerEventType.WarDeclaration), false, false, false, "ThreatBig");
        Check(Detail(ServerEventType.Trade), false, false, false, "PositiveEvent");
        Check(Detail(ServerEventType.Raid, "{\"Settlement\":null}"), false, false, false, "ThreatBig");
        Check(Detail(ServerEventType.Raid, "{\"Message\":\"Settlement\"}"), false, false, false, "ThreatBig");
        Check(Detail(ServerEventType.Raid, "{\"Settlement\":{}}"), false, false, false, "ThreatBig", detached: true);
        Check(Detail(ServerEventType.Raid, "{\"AttackerLoss\":{}}"), false, false, true, "ThreatBig", detached: true, attackerLoss: true);
        foreach (int severity in new[] { 0, 1, 2, 99 })
        {
            var notification = Detail(ServerEventType.ServerNotification,
                "{\"Title\":\"服务器维护\",\"Message\":\"完整通知\",\"Severity\":" + severity + "}");
            Check(notification, false, false, false, severity == 2 ? "ThreatBig" : severity == 1 ? "NegativeEvent" : "NeutralEvent");
            var payload = ReadServerNotificationPayloadForDisplay(notification);
            Require(payload?.Title == "服务器维护" && payload.Message == "完整通知", "Notification contents");
        }

        var timeout = Detail(ServerEventType.ServerNotification,
            "{\"NotificationId\":\"raid-timeout:raid\",\"RelatedEventType\":" + (int)ServerEventType.Raid
            + ",\"RelatedUserId\":\"defender\",\"RelatedColonyId\":\"defender-colony\"}");
        timeout.Target = new ModProtocolIdentityDto { UserId = "defender", ColonyId = "defender-colony" };
        foreach (string group in Groups)
            Require(!ShouldPostEventLetter(timeout, group), "Defender timeout duplicates must be suppressed");
        timeout.Target.UserId = "attacker";
        Require(ShouldPostEventLetter(timeout, "DirectlyProcessable"), "Attacker timeout notification must remain visible");
        timeout.Target.UserId = "defender";
        timeout.Target.ColonyId = "different-colony";
        Require(ShouldPostEventLetter(timeout, "DirectlyProcessable"), "Timeout identity must include colony");
    }

    private static readonly string[] Groups =
    {
        "WaitingForUserChoice", "DirectlyProcessable", "DeliveredUnconfirmed", "Conflicts", "Rejected",
        "", "Unknown", "等待确认", "directlyprocessable"
    };

    private static void Check(ModEventDetailDto detail, bool accept, bool reject, bool automatic, string tone,
        bool detached = false, bool attackerLoss = false)
    {
        Require(CanAutoApplyDirectlyProcessableEvent(detail) == automatic, "Automatic application eligibility");
        Require(ResolveLetterDef(detail).defName == tone, "Letter tone");
        Require(ShouldPostEventLetter(detail, null) == detached, "Unqueued settlement visibility");
        for (int i = 0; i < Groups.Length; i++)
        {
            string group = Groups[i];
            bool active = i < 2;
            bool visible = attackerLoss ? i >= 1 && i <= 4 : i == 0 || (i is 1 or 2 && !automatic) || i is 3 or 4;
            Require(ShouldPostEventLetter(detail, group) == visible, "Queue letter visibility: " + group);
            Require(CanRunEventLetterAction(detail, ClashOfRimEventLetterActionKind.Accept, group) == (active && accept), "Accept execution gate");
            Require(CanRunEventLetterAction(detail, ClashOfRimEventLetterActionKind.Reject, group) == (active && reject), "Reject execution gate");
            Require(!CanRunEventLetterAction(detail, ClashOfRimEventLetterActionKind.JumpToTarget, group)
                && !CanRunEventLetterAction(detail, ClashOfRimEventLetterActionKind.Postpone, group), "Local actions cannot submit server decisions");
            foreach (bool location in new[] { false, true })
            {
                var expected = new List<ClashOfRimEventLetterActionKind>();
                if (active && accept) expected.Add(ClashOfRimEventLetterActionKind.Accept);
                if (active && reject) expected.Add(ClashOfRimEventLetterActionKind.Reject);
                if (location) expected.Add(ClashOfRimEventLetterActionKind.JumpToTarget);
                if (expected.Count > 0) expected.Add(ClashOfRimEventLetterActionKind.Postpone);
                Require(BuildEventLetterActions(detail, location, group).SequenceEqual(expected), "Rendered buttons must agree with execution gate: " + group);
            }
        }
        detail.EventId = "";
        Require(!ShouldPostEventLetter(detail, "WaitingForUserChoice") && !ShouldPostEventLetter(detail, null), "Missing event identity cannot produce a letter");
        detail.EventId = "event";
    }

    private static ModEventDetailDto Detail(ServerEventType type, string json = "{}") => new()
    {
        EventId = "event",
        EventType = type,
        PayloadType = Enum.TryParse(type.ToString(), out EventPayloadType payloadType) ? payloadType : EventPayloadType.Unknown,
        PayloadSummary = json
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
