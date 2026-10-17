using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.Protocol;
using RimWorld;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using HarmonyLib;
using Verse;

internal static class EventCommandPresentationTests
{
    private static Dictionary<string, string> translations = new();
    private static readonly List<(string? User, FactionRelationKind Relation)> relations = new();
    private static bool relationAvailable;
    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.EventConfirmationPresentation");
        harmony.Patch(AccessTools.Method(typeof(ClashOfRimText), "Key", new[] { typeof(string) }),
            prefix: new HarmonyMethod(typeof(EventCommandPresentationTests), nameof(Translate)));
        harmony.Patch(AccessTools.Method(typeof(ClashOfRimText), "Key", new[] { typeof(string), typeof(NamedArgument[]) }),
            prefix: new HarmonyMethod(typeof(EventCommandPresentationTests), nameof(TranslateArguments)));
        harmony.Patch(AccessTools.Method(typeof(PlayerFactionProxyUtility), nameof(PlayerFactionProxyUtility.SetPlayerRelation)),
            prefix: new HarmonyMethod(typeof(EventCommandPresentationTests), nameof(ApplyRelation)));
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Languages"))) root = root.Parent;
            if (root is null) throw new Exception("Cannot locate shipped translations.");
            var presentation = new ClientEventConfirmationPresentation(static (_, _) => { });
            foreach (var culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "Languages"), "ClashOfRim.xml", SearchOption.AllDirectories))
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                translations = XDocument.Load(file).Root!.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
                DecisionPresentation();
                ApplicationPresentation();
                ReadPresentation();
                BankPresentation();
                MercenaryPresentation();
                LocalSnapshotPresentation();
                GiftPresentation();
                TradePresentation();
                ShopPresentation();
                foreach (var purpose in Enum.GetValues<ClientEventConfirmationPurpose>())
                {
                    var notice = new ClientEventConfirmationNotice(purpose, ClientEventConfirmationStage.Uploading,
                        "event-é", 7, "snapshot", Rejected: 2, Pending: 4);
                    RequireResolved(presentation.Format(notice));
                    foreach (var failure in new[]
                    {
                        new ClientEventPreparationFailure(ClientEventPreparationFailureKind.Busy),
                        new ClientEventPreparationFailure(ClientEventPreparationFailureKind.Admission, Admission: LocalMutationAdmission.NotConfigured),
                        new ClientEventPreparationFailure(ClientEventPreparationFailureKind.Admission, Admission: LocalMutationAdmission.SnapshotMissing),
                        new ClientEventPreparationFailure(ClientEventPreparationFailureKind.Blocked,
                            Blocked: new(LocalMutationKind.Bank, "bank-pending", default))
                    }) RequireResolved(presentation.Format(notice with { Stage = ClientEventConfirmationStage.PreparationFailed, PreparationFailure = failure }));
                    foreach (var kind in Enum.GetValues<ClientSnapshotTransactionFailureKind>())
                    {
                        string text = presentation.Format(notice with { Stage = ClientEventConfirmationStage.Failed,
                            Problem = new(kind, "error-code", "error-message", "validation-detail", new InvalidOperationException("exception"), false) });
                        RequireResolved(text);
                        if (!text.Contains("error-message") || !text.Contains(kind == ClientSnapshotTransactionFailureKind.Exception ? "InvalidOperationException" : "error-code"))
                            throw new Exception("Confirmation failure lost semantic parameters: " + purpose);
                        if (kind == ClientSnapshotTransactionFailureKind.Rejected && purpose != ClientEventConfirmationPurpose.AutomaticEvents
                            && !text.Contains("validation-detail")) throw new Exception("Single rejection lost server validation.");
                    }
                    // Raid acceptance is guarded and presented by the raid domain. It never builds a new package here.
                    if (purpose != ClientEventConfirmationPurpose.RaidSettlement)
                    {
                        RequireResolved(presentation.Format(notice with { Stage = ClientEventConfirmationStage.Accepted }));
                        string build = presentation.Format(notice with { Stage = ClientEventConfirmationStage.PreparationFailed,
                            PreparationFailure = new(ClientEventPreparationFailureKind.Build,
                                SnapshotFailure: new("capture", "save-reason", SnapshotUploadMessage.SaveFailed)) });
                        if (!build.Contains("save-reason")) throw new Exception("Build failure lost reason.");
                        RequireResolved(build);
                    }
                    if (purpose is not (ClientEventConfirmationPurpose.RaidSettlement or ClientEventConfirmationPurpose.AutomaticEvents))
                        RequireResolved(presentation.Format(notice with { Stage = ClientEventConfirmationStage.Empty }));
                }
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; harmony.UnpatchAll(harmony.Id); translations.Clear(); }
    }

    private static void DecisionPresentation()
    {
        foreach (var kind in new[] { ClientEventDecisionKind.RejectGift, ClientEventDecisionKind.RejectSupport,
            ClientEventDecisionKind.AcceptDiplomacy, ClientEventDecisionKind.RejectDiplomacy })
        foreach (var stage in Enum.GetValues<ClientEventDecisionStage>())
        {
            var notice = new ClientEventDecisionNotice(kind, stage, "event-é", "code", "server-message", new InvalidOperationException("exception"),
                LocalMutationAdmission.SnapshotMissing, Receipt: new(new() { Accepted = true }, "return-event", true));
            RequireResolved(ClientEventDecisionPresentation.Format(notice));
            if (stage == ClientEventDecisionStage.Accepted && kind is ClientEventDecisionKind.AcceptDiplomacy or ClientEventDecisionKind.RejectDiplomacy)
            {
                string duplicate = ClientEventDecisionPresentation.Format(notice with
                { Receipt = new(new() { Accepted = true, ErrorCode = (int)ProtocolErrorCode.DuplicateRequest, Message = "already processed" }) });
                if (duplicate != "already processed") throw new Exception("Duplicate diplomacy response was presented as a newly accepted/rejected request.");
            }
        }
        int refreshed = 0;
        var runtime = new ClientEventDecisionRuntime(_ => refreshed++);
        relations.Clear(); relationAvailable = true;
        runtime.ApplyDiplomacyRelation("actor", "hOsTiLe", ServerEventType.AllianceRequest);
        runtime.ApplyDiplomacyRelation("actor", "Neutral", ServerEventType.AllianceRequest);
        relationAvailable = false;
        runtime.ApplyDiplomacyRelation("actor", "Ally", ServerEventType.AllianceRequest);
        runtime.ApplyDiplomacyRelation("actor", "unknown", ServerEventType.AllianceRequest);
        runtime.ApplyDiplomacyRelation("actor", null, ServerEventType.AllianceRequest);
        if (!relations.Select(r => r.Relation).SequenceEqual(new[] { FactionRelationKind.Hostile, FactionRelationKind.Neutral, FactionRelationKind.Ally })
            || relations.Any(r => r.User != "actor") || refreshed != 5)
            throw new Exception("Diplomacy adapter guessed an unknown relation or failed to request projection refresh.");
    }

    private static void ApplicationPresentation()
    {
        var presentation = new ClientEventApplicationPresentation(static (_, _) => { });
        foreach (var reason in Enum.GetValues<ClientEventApplicationReason>()) RequireResolved(presentation.Reason(reason));
        foreach (bool automatic in new[] { false, true })
        foreach (var kind in Enum.GetValues<ClientEventApplicationKind>())
        foreach (var stage in Enum.GetValues<ClientEventApplicationStage>())
        {
            var prepared = ItemDeliveryClientProcessingResult.Failed(ItemDeliveryClientProcessingResultKind.PayloadParseFailed, "prepare-reason");
            var notice = new ClientEventApplicationNotice(stage, automatic, "event-é", kind, "application-reason",
                new InvalidOperationException("application-reason"), Count: 7, Pending: 2,
                Admission: LocalMutationAdmission.PendingMutation, Mutation: new(LocalMutationKind.Bank, "bank", default), Prepared: prepared);
            RequireResolved(presentation.Format(notice));
            if (stage == ClientEventApplicationStage.Exception && !presentation.Format(notice).Contains("InvalidOperationException"))
                throw new Exception("Application exception lost its type.");
        }
        foreach (string mode in new[] { "DropPod", "MapEdge", "CenterNear", "", "custom" })
        {
            var applied = GiftLandingApplicationResult.Applied("event", 3, 4, mode, true);
            RequireResolved(presentation.Format(new(ClientEventApplicationStage.Applied, Gift: applied, Pending: 2)));
        }
        RequireResolved(presentation.Format(new(ClientEventApplicationStage.Prepared,
            Prepared: ItemDeliveryClientProcessingResult.Accepted(new("event", null, "map", null, "DropPod", Array.Empty<GiftItemReference>(), true)))));
        RequireResolved(presentation.Format(new(ClientEventApplicationStage.Prepared,
            Prepared: ItemDeliveryClientProcessingResult.Rejected(new("event", "user", "colony", "snapshot", "reason")))));
    }

    private static void ReadPresentation()
    {
        var presentation = new ClientEventReadPresentation();
        var ports = new EventModuleTestPorts();
        var session = new ClientSessionLifecycle(ports, ports, new ClientSessionConnection(ports));
        var reader = new ClientEventReader(session, new ClientEventInbox(), new EventReadTestProtocol(), ports);
        RequireResolved(presentation.MergedReason); RequireResolved(presentation.ManualDetailsReason);
        foreach (var kind in Enum.GetValues<ClientEventReadKind>())
        {
            foreach (var stage in Enum.GetValues<ClientEventRefreshStage>().Where(s => s != ClientEventRefreshStage.Updated))
                RequireResolved(presentation.Format(new(stage, kind, "reason", "deferred", LocalMutationAdmission.PendingMutation,
                    new(LocalMutationKind.Bank, "pending", default), Queue: EventReadTestProtocol.QueueOf("é"))));
            var request = reader.Capture(kind);
            foreach (var stage in Enum.GetValues<ClientEventReadStage>())
            {
                var update = new ClientEventReadUpdate(request, stage, Queue: new()
                {
                    WaitingForUserChoice = new() { new() { EventId = "é", EventType = ServerEventType.AllianceRequest,
                        Status = "ReadyForImmediateDelivery", DeliverySemantics = 2, RequiresSnapshotConfirmation = true } }
                }, Details: new[] { new ModEventDetailDto { EventId = "é", EventType = ServerEventType.ItemDelivery, Status = "DeliveredToClient" } });
                var notice = new ClientEventRefreshNotice(ClientEventRefreshStage.Updated, kind, Update: update);
                RequireResolved(presentation.Format(notice));
                RequireResolved(presentation.Format(notice with { Update = update with { Empty = true, Changed = true } }));
                foreach (var failure in Enum.GetValues<ClientEventReadFailureKind>())
                {
                    var failed = update with { Failure = new(failure, "code", "read-reason", new InvalidOperationException("read-reason")) };
                    string text = presentation.Format(notice with { Update = failed });
                    RequireResolved(text);
                    if (!text.Contains("code") || !text.Contains("read-reason")) throw new Exception("Read failure lost diagnostic fields.");
                }
            }
        }
    }

    private static void ShopPresentation()
    {
        var view = new AIRsLight.ClashOfRim.ServerShop.ClientShopPresentation();
        RequireResolved(view.NotAdministrator);
        RequireResolved(view.Busy); RequireResolved(view.BaseSnapshotChanged); RequireResolved(view.SnapshotBuildFailed("capture-reason"));
        foreach (var admission in new[] { LocalMutationAdmission.NotConfigured, LocalMutationAdmission.SnapshotMissing, LocalMutationAdmission.PendingMutation })
            RequireResolved(view.Admission(admission, new(LocalMutationKind.Shop, "pending", default)));
        foreach (string key in new[] { "StatusIdle", "StatusRefreshing", "StatusSavingListing", "StatusRemovingListing", "StatusMissingListing", "StatusListingSaved", "StatusListingRemoved" })
            RequireResolved(view.Text(key));
        RequireResolved(view.Text("StatusRefreshed", ("COUNT", 17)));
        foreach (string key in new[] { "StatusItemCountOverflow", "StatusConfirmationIncomplete", "StatusApplyingSale", "StatusApplyingPurchase", "StatusUploadingPurchase", "StatusMissingListing", "StatusStockNotEnough", "StatusPriceOverflow" })
            RequireResolved(view.Text(key));
        foreach (string key in new[] { "StatusPurchased", "StatusSoldToServer" }) RequireResolved(view.Text(key, ("COUNT", 3), ("PRICE", 40)));
        RequireResolved(view.Text("StatusException", ("TYPE", "InvalidOperationException"), ("MESSAGE", "exception")));
        foreach (string prefix in new[] { "StatusRefresh", "StatusSave", "StatusRemove", "StatusPurchase" })
        foreach (string suffix in new[] { "Failed", "Rejected" })
        {
            string text = view.Text(prefix + suffix, ("CODE", "error-code"), ("MESSAGE", "server-message"));
            RequireResolved(text);
            if (!text.Contains("error-code") || !text.Contains("server-message")) throw new Exception("Shop status lost server diagnostic fields.");
        }
    }

    private static void TradePresentation()
    {
        var view = new AIRsLight.ClashOfRim.Trades.ClientTradeMarketPresentation();
        RequireResolved(view.InitialStatus); RequireResolved(view.Busy);
        foreach (var admission in new[] { LocalMutationAdmission.NotConfigured, LocalMutationAdmission.SnapshotMissing, LocalMutationAdmission.PendingMutation })
            RequireResolved(view.Admission(admission, new(LocalMutationKind.Trade, "pending", default)));
        foreach (string key in new[] { "Disabled", "NoAcceptableOrders", "PageStatus", "StatusRefreshingMarket", "StatusLoadingMoreMarket",
            "StatusMarketRefreshFailed", "StatusMarketRejected", "StatusMarketException", "StatusAcceptingOrder", "StatusAcceptedOrder", "StatusAcceptMemoExists",
            "StatusAcceptFailed", "StatusAcceptRejected", "StatusAcceptException", "StatusMissingOrderEventId", "StatusCancelNotAllowed", "StatusCancelAcceptedMemoNotAllowed",
            "StatusCancellingAcceptedMemo", "StatusCancellingOrder", "StatusCancelSucceeded", "StatusCancelFailed", "StatusCancelRejected", "StatusCancelException",
            "StatusCancelAcceptedMemoSucceeded", "StatusCancelAcceptedMemoFailed", "StatusCancelAcceptedMemoRejected", "StatusCancelAcceptedMemoException" })
            RequireResolved(view.Text(key, ("LOADED", 3), ("TOTAL", 5), ("CODE", "rejected"), ("MESSAGE", "server-message"),
                ("TYPE", "Exception"), ("EVENTID", "order"), ("MEMOID", "memo"), ("COUNT", 2)));
        var operations = new AIRsLight.ClashOfRim.Trades.ClientTradeOperationsPresentation();
        foreach (string key in new[] { "StatusDraftMissingThings", "StatusOrderUnavailable", "StatusConfirmationIncomplete", "StatusMissingAcceptMemo",
            "StatusCaravanFulfillPreparing", "StatusSubmittingCaravanFulfill", "StatusOrderReserving", "StatusDropPodReserving",
            "StatusOrderCreatedReservationFailed", "StatusFulfillLocalApplyFailed", "StatusOrderPackagingSnapshot", "StatusFulfillPackagingSnapshot",
            "StatusOrderSnapshotBuildFailed", "StatusFulfillSnapshotBuildFailed", "StatusOrderSubmittingTransaction", "StatusFulfillSubmittingTransaction",
            "StatusSubmittingOrder", "StatusSubmittingDropPodFulfill", "StatusOrderCreatedReserved", "StatusDropPodFulfillCreated",
            "StatusOrderSubmitException", "StatusCreateRejected", "MissingSuffix", "StatusFulfillFailed", "StatusFulfillRejected", "StatusFulfillException" })
            RequireResolved(operations.Text("ClashOfRim.Trade." + key, ("EVENTID", "order"), ("MESSAGE", "message"), ("REASON", "reason"),
                ("FEE", 12), ("ACCEPTOR", "acceptor"), ("OWNER", "owner"), ("TYPE", "Exception"), ("CODE", "rejected"), ("MISSING", "Steel")));
    }

    private static void MercenaryPresentation()
    {
        var view = new AIRsLight.ClashOfRim.Mercenaries.ClientMercenaryPresentation();
        RequireResolved(view.Idle); RequireResolved(view.BaseSnapshotChanged);
        foreach (var admission in new[] { LocalMutationAdmission.NotConfigured, LocalMutationAdmission.SnapshotMissing, LocalMutationAdmission.PendingMutation })
            RequireResolved(view.Admission(admission, new(LocalMutationKind.Mercenary, "pending", default)));
        foreach (string key in new[] { "PriceLine", "StatusQuoteFailed", "StatusQuoteRejected", "StatusQuoteException",
            "StatusHiring", "GuardStatusHiring", "StatusHireFailed", "StatusHireRejected", "StatusMissingContract", "StatusNoMap",
            "StatusInsufficientSilver", "StatusArrivalPawnMissing", "StatusConfirmationFailed", "StatusBusy", "StatusUploadingConfirmation",
            "StatusConfirmationIncomplete", "StatusConfirmationSucceeded", "StatusHiredConfirmed", "GuardStatusConfirmed", "StatusException" })
            RequireResolved(view.Text(key, ("PRICE", 350), ("CODE", "Conflict"), ("MESSAGE", "server-message"), ("TYPE", "IOException"), ("NEEDED", 350), ("AVAILABLE", 10), ("SNAPSHOT", "snapshot")));
        if (view.Text("PriceLine", ("PRICE", 350)) != AIRsLight.ClashOfRim.Mercenaries.MercenaryUiUtility.FormatPriceLine(350))
            throw new Exception("Mercenary quote presentation changed its price text.");
    }

    private static void LocalSnapshotPresentation()
    {
        var view = new ClientLocalSnapshotPresentation(_ => { });
        RequireResolved(view.Busy); RequireResolved(view.BaseSnapshotChanged);
        RequireResolved(view.Admission(LocalMutationAdmission.PendingMutation, new(LocalMutationKind.Support, "pending", default)));
        RequireResolved(view.Failed("status", "code", "reason")); RequireResolved(view.Succeeded("snapshot"));
        RequireResolved(view.Exception("status", new InvalidOperationException("reason")));
        RequireResolved(view.Result(new(null, null, new("code", null, SnapshotUploadMessage.NoRunningGame))).Message!);
    }

    private static void BankPresentation()
    {
        var bank = new ClientBankPresentation();
        foreach (string key in new[] { "StatusNotPulled", "StatusRefreshing", "StatusBusy", "StatusNoMap", "StatusNoActiveLoan",
            "StatusMissingDebt", "StatusLoansDisabled", "StatusBaseSnapshotChanged", "StatusUploadingConfirmation",
            "StatusCreateMinimumLoan", "StatusCreateMaxLoanExceeded", "StatusCreateDurationInvalid", "StatusCreateRejected",
            "StatusInsufficientSilver", "StatusConfirmationFailed", "StatusCreated", "StatusRepaid", "StatusDebtRepaid",
            "StatusRefreshFailed", "StatusRefreshRejected", "StatusRepayFailed", "StatusRepayRejected", "StatusDebtRepayFailed",
            "StatusDebtRepayRejected", "StatusException" })
            RequireResolved(bank.Text(key, ("MIN", 100), ("MAX", 500), ("NEEDED", 100), ("AVAILABLE", 90),
                ("CODE", "Conflict"), ("MESSAGE", "message"), ("TYPE", "IOException"), ("PRINCIPAL", 100), ("TOTAL", 120)));
        RequireResolved(bank.Summary(new() { ColonyWealth = 10000, MinLoanSilver = 100, MaxLoanSilver = 500, BaseAnnualInterestRate = .1f }));
        RequireResolved(bank.Summary(new() { ActiveLoan = new() { LoanId = "é", TotalDueSilver = 120, Status = "Active" } }));
    }

    private static void GiftPresentation()
    {
        var gift = new ClientGiftPresentation(static _ => { });
        foreach (string key in new[] { "Disabled", "StatusTargetIncomplete", "StatusTargetTileMismatch", "StatusNoSelectedItems",
            "StatusPackagingSnapshot", "StatusReserving", "StatusLocalRemoveFailed", "ReasonTransportPodGift", "StatusSnapshotBuildFailed",
            "StatusSubmittingTransaction", "StatusSubmittingForced", "StatusSubmittingNormal", "StatusCreateFailed", "StatusCreateRejected",
            "StatusCreateException", "StatusForcedCreated", "StatusNormalCreated", "StatusNoTargets", "TargetOption" })
            RequireResolved(gift.Text("ClashOfRim.GiftDelivery." + key, ("MESSAGE", "message"), ("REASON", "reason"),
                ("TARGET", "target"), ("CODE", "Conflict"), ("TYPE", "IOException"), ("EVENTID", "gift")));
        RequireResolved(gift.Text("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged"));
        RequireResolved(gift.Text("ClashOfRim.Raid.PvpDisabled"));
        RequireResolved(gift.Text("ClashOfRim.SnapshotUpload.Busy"));
    }

    private static bool ApplyRelation(string? userId, FactionRelationKind relationKind, ref bool __result)
    { relations.Add((userId, relationKind)); __result = relationAvailable; return false; }

    private static bool Translate(string key, ref string __result)
    {
        if (!translations.TryGetValue(key, out var value)) throw new Exception("Unknown confirmation translation: " + key);
        __result = value;
        return false;
    }
    private static bool TranslateArguments(string key, NamedArgument[] args, ref string __result)
    {
        Translate(key, ref __result);
        foreach (var arg in args) __result = __result.Replace("{" + arg.label + "}", Convert.ToString(arg.arg, CultureInfo.CurrentCulture));
        return false;
    }
    private static void RequireResolved(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('{') || text.Contains('}'))
            throw new Exception("Unresolved confirmation text: " + text);
    }
}
