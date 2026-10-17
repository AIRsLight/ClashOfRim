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
using AIRsLight.ClashOfRim.Protocol;
using RimWorld;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.ClientNetwork;
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
                    foreach (var kind in Enum.GetValues<ClientEventConfirmationFailureKind>())
                    {
                        string text = presentation.Format(notice with { Stage = ClientEventConfirmationStage.Failed,
                            Problem = new(kind, "error-code", "error-message", "validation-detail", new InvalidOperationException("exception"), false) });
                        RequireResolved(text);
                        if (!text.Contains("error-message") || !text.Contains(kind == ClientEventConfirmationFailureKind.Exception ? "InvalidOperationException" : "error-code"))
                            throw new Exception("Confirmation failure lost semantic parameters: " + purpose);
                        if (kind == ClientEventConfirmationFailureKind.Rejected && purpose != ClientEventConfirmationPurpose.AutomaticEvents
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
