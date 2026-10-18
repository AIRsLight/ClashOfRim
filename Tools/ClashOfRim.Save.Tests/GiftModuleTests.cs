using System.Globalization;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Gifts;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

internal static class GiftModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (bool online in new[] { false, true }) CreateReplayAndReject(factory, online);
                foreach (bool final in new[] { false, true }) RevokedByRecovery(factory, final);
                ForcedDelivery(factory);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
        var retired = (EventCreationResponse)((IValueHttpResult)GiftEndpoints.CreateGift()).Value!;
        Require(!retired.Result.Accepted && retired.DeliverySemantics == ProtocolDeliverySemantics.ServerNotification,
            "Retired gift endpoint changed its rejection contract.");
    }

    private static CreateGiftWithSnapshotRequest Request(EconomicModuleTestContext f, string key = "gift-é", string? kind = null) =>
        new(new CreateGiftRequest(key, new("userA", "colonyA", "before"), new("userB", "colonyB", null),
            [new("Silver_1", "Silver", 13)], "礼物-İ", deliveryKind: kind), ProtocolDtoMapper.ToMetadataDto(f.Package));

    private static void CreateReplayAndReject(Func<string, SaveSnapshotPackage> factory, bool online)
    {
        var f = new EconomicModuleTestContext(factory);
        OnlinePresenceLease? presence = null;
        if (online) f.Presence.TryConnectExclusive("userB", out presence);
        using (presence)
        {
            var request = Request(f);
            Require(!f.Gifts.CreateWithSnapshot(request, f.Package.Payload, "invalid").Result.Accepted
                && f.Recovery.Replays == 0, "Invalid principal started gift recovery.");
            string wrong = f.Issue("other", "elsewhere");
            Require(!f.Gifts.CreateWithSnapshot(request, f.Package.Payload, wrong).Result.Accepted
                && f.Recovery.Replays == 0, "Different principal borrowed gift actor identity.");
            var created = f.Gifts.CreateWithSnapshot(request, f.Package.Payload, f.Token);
            Require(created.Result.Accepted && created.AppliedSnapshotId == "paid"
                && created.NextLineageToken == f.Snapshots.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken
                && created.DeliverySemantics == (online ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending)
                && f.Recovery.Commits == 1 && f.Raids.Reconciliations == 0, "Gift creation lost snapshot, ownership or delivery semantics.");
            var source = f.Ledger.Find(created.EventId!)!;
            Require(source.Payload is ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.Gift } gift
                && gift.Items.Single().SourceSnapshotId == "paid" && gift.Items.Single().StackCount == 13,
                "Gift did not bind items to the accepted snapshot.");
            f.Configuration = new(giftsEnabled: false);
            long notified = f.Notifications.GetVersion("userB");
            var replay = f.Gifts.CreateWithSnapshot(request, f.Package.Payload, f.Token);
            Require(replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.EventId == created.EventId
                && f.Recovery.Commits == 1 && f.Notifications.GetVersion("userB") == notified,
                "Configuration change replaced a committed gift receipt or repeated effects.");
            Require(f.Gifts.CreateWithSnapshot(request, [1], f.Token).Result.ErrorCode == ProtocolErrorCode.Conflict,
                "Gift replay ignored actual snapshot bytes.");
            Require(!f.Gifts.CreateWithSnapshot(Request(f, "fresh"), f.Package.Payload, f.Token).Result.Accepted,
                "Disabled gifts accepted a fresh operation.");

            string receiver = f.Issue("userB", "colonyB");
            var reject = new RejectGiftRequest(source.EventId, "userB", "colonyB", "base", "first", receiver);
            var returned = f.Gifts.Reject(reject);
            var again = f.Gifts.Reject(new(source.EventId, "userB", "colonyB", "base", "second", receiver));
            Require(returned.ReturnEventCreated && again.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                && again.ReturnEventId == returned.ReturnEventId && f.Ledger.ListAll().Count == 2
                && f.Ledger.Find(source.EventId)!.DecisionReason == "first"
                && f.Notifications.GetVersion("userB") == notified + 2,
                "Gift rejection replaced the decision, duplicated returns or failed to repeat notifications.");
        }
    }

    private static void RevokedByRecovery(Func<string, SaveSnapshotPackage> factory, bool final)
    {
        var f = new EconomicModuleTestContext(factory);
        f.Recovery.OnReplay = () => { if (f.Recovery.Replays == (final ? 2 : 1)) f.Sessions.EndUser("userA"); };
        var result = f.Gifts.CreateWithSnapshot(Request(f), f.Package.Payload, f.Token);
        Require(result.Result.ErrorCode == ProtocolErrorCode.SessionUnavailable && f.Recovery.Commits == 0
            && f.Ledger.ListAll().Count == 0 && f.Snapshots.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before",
            "Gift committed after recovery revoked its credentials.");
    }

    private static void ForcedDelivery(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string change in new[] { "none", "logout", "blocked", "cooldown", "peace", "online", "disabled", "moved" })
        {
            var f = new EconomicModuleTestContext(factory);
            f.Configuration = new(pvpEnabled: true);
            f.Relations.SetRelationKind("userA", "colonyA", "userB", "colonyB", DiplomacyRelationRegistry.RelationHostile, null, DateTimeOffset.UtcNow);
            void SetTarget(string map)
            {
                var target = f.Package with { Envelope = f.Package.Envelope with { Identity = new("userB", "colonyB", "target") },
                    Index = f.Package.Index with { Maps = f.Package.Index.Maps.Select(m => m with { UniqueId = map }).ToArray() } };
                f.Snapshots.StoreLatest(new(target.Envelope.Identity, target.Envelope, target.Index, DateTimeOffset.UtcNow));
            }
            SetTarget("Map_old");
            f.Raids.OnReconcile = () => { if (change == "logout") f.Sessions.EndUser("userA"); };
            OnlinePresenceLease? presence = null;
            f.Recovery.OnReplay = () =>
            {
                if (f.Recovery.Replays != 2) return;
                switch (change)
                {
                    case "blocked": f.Raids.BlockingRaid = AuthoritativeEventFactory.Create(ServerEventType.Raid,
                        new("attacker", "colony"), new("userB", "colonyB"), "raid", false,
                        new RaidEventPayload("target", null, null, null, null), DateTimeOffset.UtcNow); break;
                    case "cooldown": f.Raids.CooldownUntil = DateTimeOffset.UtcNow.AddHours(1); break;
                    case "peace": f.Relations.SetRelationKind("userA", "colonyA", "userB", "colonyB", DiplomacyRelationRegistry.RelationNeutral, null, DateTimeOffset.UtcNow); break;
                    case "online": f.Presence.TryConnectExclusive("userB", out presence); break;
                    case "disabled": f.Configuration = new(pvpEnabled: false); break;
                    case "moved": SetTarget("Map_new"); break;
                }
            };
            try
            {
                var result = f.Gifts.CreateWithSnapshot(Request(f, kind: "forced"), f.Package.Payload, f.Token);
                bool accepted = change is "none" or "moved";
                Require(result.Result.Accepted == accepted && f.Recovery.Commits == (accepted ? 1 : 0)
                    && f.Raids.Reconciliations == 1, "Forced gift bypassed final protection or repeated raid recovery: " + change);
                if (accepted)
                {
                    var item = f.Ledger.Find(result.EventId!)!;
                    Require(item.RejectionPolicy == EventRejectionPolicy.NotRejectable
                        && item.TargetContext?.MapUniqueId == (change == "moved" ? "Map_new" : "Map_old"),
                        "Forced gift retained stale destination or became rejectable.");
                    f.Configuration = new(giftsEnabled: false, pvpEnabled: false);
                    Require(f.Gifts.CreateWithSnapshot(Request(f, kind: "forced"), f.Package.Payload, f.Token).Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                        && f.Raids.Reconciliations == 1, "Forced gift receipt triggered raid recovery again.");
                }
            }
            finally { presence?.Dispose(); }
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
