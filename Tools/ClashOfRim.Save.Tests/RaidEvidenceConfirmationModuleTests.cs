using System.Globalization;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class RaidEvidenceConfirmationModuleTests
{
    internal static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (bool prepared in new[] { false, true }) ConfirmAndReplay(prepared);
            }
            foreach (string identity in new[] { "header", "metadata", "defender", "owner" }) RejectIdentity(identity);
            foreach (int boundary in new[] { 1, 2, 3 })
                foreach (string change in new[] { "none", "logout", "replace", "cancel" }) Revalidate(boundary, change);
            foreach (int boundary in new[] { 2, 3 }) Revalidate(boundary, "source");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void ConfirmAndReplay(bool prepared)
    {
        using var f = new Fixture(prepared);
        var c = f.Context;
        Check(f.Confirmation.Handles(c.Data.Raid.EventId, null)
            && !f.Confirmation.Handles(c.Data.Raid.EventId, "loss-source") && !f.Confirmation.Handles("missing", null),
            "Evidence dispatch confused original raids with loss confirmation.");
        var response = f.Confirm();
        Check(response.Result.Accepted && response.ServerValidationResult == "SettlementQueued", "Independent confirmation failed: " + response.Result.Message);
        var job = c.Jobs.ListAll().Single();
        var payload = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
        Check(job.State == SnapshotPostUploadJobState.Ready && f.Pipeline.Calls == 1
            && f.Pipeline.UsedPreparation == prepared && payload.RequestHash == RaidEvidenceRequestBinding.Hash(f.Request(), c.Request.EvidencePayload)
            && response.NextLineageToken == c.Snapshots.GetLatest("attacker", "attacker-colony")!.Envelope.NextLineageToken,
            "Confirmation lost request binding, lineage or the preparation path.");
        f.Token = f.Renew("attacker", "attacker-colony");
        var replay = f.Confirm();
        Check(replay.Result.Accepted && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && c.Jobs.ListAll().Single() == job && f.Pipeline.Last!.Job.OccurredAtUtc == job.OccurredAtUtc
            && replay.NextLineageToken == response.NextLineageToken, "Renewed-session retry changed the original receipt.");
        var conflict = f.Confirm(f.Request(result: "different-result"));
        Check(!conflict.Result.Accepted && f.Pipeline.Calls == 2, "Conflicting evidence ran plugin effects.");
        var settlement = new RaidSettlement(c.Ledger, c.Snapshots, c.Mutations, new SnapshotCommitService(), new BankLoanRegistry(), c.Players,
            new EventNotificationHub(), new OnlinePresenceRegistry(), new AdminBaselineRegistry(), new ServerPluginRegistry(),
            new CompatibilityBaselineRegistry(), () => new(), () => NullLogger.Instance);
        var completed = settlement.Execute(payload, () => c.Artifacts.Read(payload.EvidenceArtifactId)!, c.Data.Now.AddMinutes(1));
        Check(completed.Kind == RaidSettlementOperationResultKind.Completed, "Fixture settlement failed: " + completed.Message);
        c.Jobs.MarkCompleted(job.JobId);
        c.Artifacts.Delete(payload.EvidenceArtifactId);
        replay = f.Confirm();
        Check(replay.Result.Accepted && replay.ServerValidationResult == "AlreadyApplied"
            && replay.AppliedSnapshotId == "evidence" && replay.NextLineageToken == response.NextLineageToken
            && f.Pipeline.Calls == 2 && c.Jobs.ListAll().Count == 0, "Completed replay required deleted evidence or repeated plugin effects.");
        conflict = f.Confirm(f.Request(result: "different-result"));
        Check(!conflict.Result.Accepted && conflict.ServerValidationResult == "CompletedRequestConflict"
            && conflict.NextLineageToken is null && conflict.AppliedSnapshotId is null, "Conflicting completed replay leaked snapshot credentials.");
    }

    private static void RejectIdentity(string identity)
    {
        using var f = new Fixture(true);
        string defenderToken = f.Renew("defender", "defender-colony");
        var request = f.Request(token: identity == "metadata" ? defenderToken : null, owner: identity == "owner" ? "defender" : "attacker");
        if (identity == "defender") request = f.Request(token: defenderToken, owner: "defender", colony: "defender-colony");
        var reply = f.Confirm(request, identity is "header" or "defender" ? defenderToken : f.Token);
        Check(!reply.Result.Accepted && reply.AppliedSnapshotId is null && reply.NextLineageToken is null
            && f.Pipeline.Calls == 0 && f.Context.Recovery.Replays == 0, "Unauthorized evidence entered recovery or revealed a receipt.");
        f.Context.CheckUnpublished();
    }

    private static void Revalidate(int boundary, string change)
    {
        using var f = new Fixture(true);
        var c = f.Context;
        int captures = 0;
        c.Recovery.OnCapture = () => { if (++captures == boundary) c.Recovery.NeedsPreparation = true; };
        ConfirmEventApplicationResponse? reply = null;
        SnapshotPreparationGateProbe.Run(c.Gate, hook => c.Recovery.OnPrepare = hook, () => reply = f.Confirm(), () =>
        {
            c.CheckUnpublished();
            if (change is "logout" or "replace") f.Sessions.EndUser("attacker");
            if (change == "replace") f.Sessions.TryCreate("attacker", "attacker-colony", DateTimeOffset.UtcNow, out _);
            if (change == "cancel") c.Ledger.ChangeStatus(c.Data.Raid.EventId, ServerEventStatus.Cancelled);
            if (change == "source")
            {
                var source = c.Ledger.Find(c.Data.Raid.EventId)!;
                var original = (RaidEventPayload)source.Payload;
                c.Ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(source.EventId, source.Status, source.Status,
                    original, original with { StartedAtUtc = c.Data.Now.AddMinutes(1) })]));
            }
        });
        Check(reply!.Result.Accepted == (change == "none") && f.Pipeline.Calls == (change == "none" ? 1 : 0),
            "Confirmation failed to revalidate after unlocked recovery: " + boundary + "/" + change);
        if (change != "none") c.CheckUnpublished();
        else Check(captures == 3 && c.Recovery.Replays == 3, "Prepared confirmation repeated or omitted a recovery boundary.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly RaidEvidenceModuleTestContext Context;
        internal readonly LoginSessionRegistry Sessions = new();
        private readonly AuthTokenRegistry tokens = new();
        internal readonly PipelineProbe Pipeline;
        internal readonly RaidEvidenceConfirmation Confirmation;
        internal string Token;

        internal Fixture(bool prepared)
        {
            Context = new(prepared);
            var colonies = new ColonyAccessAuthorization(tokens, Sessions, new DiplomacyRelationRegistry(),
                new RaidPreparationRegistry(), Context.Ledger, () => true);
            var requests = new SnapshotRequestAuthorization(new(tokens, Sessions), colonies, Context.Mutations);
            Pipeline = new(Context);
            Confirmation = new(Context.Ledger, Context.Jobs, Context.Scheduling, Context.Mutations, requests, colonies,
                new(Context.Snapshots, SnapshotUploadPolicy.AllowAnyVersion, new ServerPluginRegistry(), new CompatibilityBaselineRegistry()),
                new PendingConfirmationReconciler(new BankLoanRegistry(), new MercenaryContractRegistry(), () => new(), Context.Gate),
                Pipeline, () => NullLogger.Instance);
            Token = Renew("attacker", "attacker-colony");
        }
        internal string Renew(string user, string colony)
        {
            Sessions.EndUser(user);
            Sessions.TryCreate(user, colony, DateTimeOffset.UtcNow, out string session);
            return tokens.Issue(user, user, colony, session, DateTimeOffset.UtcNow);
        }
        internal ConfirmEventApplicationMetadataRequest Request(string? token = null, string result = "Applied",
            string owner = "attacker", string colony = "attacker-colony") => new("confirmation", Context.Data.Raid.EventId, null,
                owner, colony, "attack", ProtocolDtoMapper.ToMetadataDto(new SaveSnapshotPackage(Context.Request.Snapshot.Envelope,
                    Context.Request.EvidencePayload, Context.Request.Snapshot.Index)), result, token ?? Token);
        internal ConfirmEventApplicationResponse Confirm(ConfirmEventApplicationMetadataRequest? request = null, string? principal = null) =>
            Confirmation.Confirm(request ?? Request(), Context.Request.EvidencePayload, principal ?? Token, Context.Data.Now);
        public void Dispose() => Context.Dispose();
    }

    private sealed class PipelineProbe(RaidEvidenceModuleTestContext context) : IRaidEvidencePipeline
    {
        internal int Calls;
        internal bool UsedPreparation;
        internal RaidEvidenceSchedule? Last;
        public void Run(RaidEvidenceSchedule evidence, RaidEvidenceSchedulePreparation? preparation)
        {
            Check(Monitor.IsEntered(context.Gate) && Monitor.IsEntered(context.Jobs.SnapshotCommitGate), "Pipeline ran outside the publication boundary.");
            Calls++; Last = evidence; UsedPreparation = preparation is not null;
            if (preparation is null) context.Scheduling.Schedule(evidence);
            else preparation.Commit(context.Scheduling, evidence.Snapshot.Envelope, evidence.Job.OccurredAtUtc);
        }
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
