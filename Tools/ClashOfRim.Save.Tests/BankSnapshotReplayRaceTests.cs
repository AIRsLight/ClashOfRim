using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class BankSnapshotReplayRaceTests
{
    private static readonly ProtocolIdentity Actor = new("userA", "colonyA", "before");

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        SnapshotEndpointRecoveryPreparationTests.Run(fixture);
        foreach (string kind in new[] { "borrow", "loan", "debt" })
        foreach (string mode in new[] { "same", "different", "revoke", "replace" }) RunScenario(fixture, kind, mode);
    }

    private static void RunScenario(Func<string, SaveSnapshotPackage> fixture, string kind, string mode)
    {
        var state = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(
            bankLoansEnabled: true, bankMinLoanSilver: 1, bankMinDurationDays: 1, bankBaseAnnualInterestRate: 0));
        var package = SnapshotEndpointPreparation.Prepare(state.SnapshotStore, fixture, Actor, "paid");
        var now = DateTimeOffset.UtcNow;
        string debtId = "";
        if (kind == "loan")
        {
            state.BankLoans.Create("seed", Actor.UserId, Actor.ColonyId!, "before", 100, 10, 1, 1000, now, "loan");
            state.BankLoans.ConfirmPendingForSnapshot(Actor.UserId, Actor.ColonyId!, "before", 1000, now);
        }
        if (kind == "debt") debtId = state.BankLoans.CreateDebt("seed", Actor.UserId, Actor.ColonyId!, "before", 110,
            "Test", "test debt", "source", 1000, now).DebtId;
        string Issue(ProtocolIdentity actor)
        {
            state.LoginSessions.TryCreate(actor.UserId, actor.ColonyId!, now, out string session);
            return state.AuthTokens.Issue(actor.UserId, actor.UserId, actor.ColonyId!, session, now);
        }
        string token = Issue(Actor);
        string Key(int i) => mode == "different" ? "operation-" + i : "operation";
        async Task<Reply> Call(int i, string? field = null, JsonNode? value = null, string? header = null, ProtocolIdentity? principal = null)
        {
            var metadata = ProtocolDtoMapper.ToMetadataDto(package);
            object request = kind == "borrow"
                ? new CreateBankLoanWithSnapshotRequest(Key(i), Actor.UserId, Actor.ColonyId!, "before", token, 1100, 10000, 100, 1, "loan-" + Key(i), 0, metadata)
                : kind == "loan"
                    ? new RepayBankLoanWithSnapshotRequest(Key(i), Actor.UserId, Actor.ColonyId!, "before", token, 1100, "loan", 110, metadata)
                    : new RepayBankDebtWithSnapshotRequest(Key(i), Actor.UserId, Actor.ColonyId!, "before", token, 1100, debtId, 110, metadata);
            var body = JsonSerializer.SerializeToNode(request)!.AsObject();
            if (field is not null) body[field] = value?.DeepClone();
            if (kind == "debt")
            {
                var r = await SnapshotEndpointTestClient.Invoke<BankDebtResponse>("RepayBankDebtWithSnapshot", state,
                    _ => body, package.Payload, principal ?? Actor, header ?? token);
                return new(r.Result, r.Debt?.DebtId, r.AppliedSnapshotId, r.NextLineageToken, r.SilverDelta, JsonSerializer.Serialize(r.Debt));
            }
            var loan = await SnapshotEndpointTestClient.Invoke<BankLoanResponse>(kind == "borrow" ? "CreateBankLoanWithSnapshot" : "RepayBankLoanWithSnapshot",
                state, _ => body, package.Payload, principal ?? Actor, header ?? token);
            return new(loan.Result, loan.Loan?.LoanId, loan.AppliedSnapshotId, loan.NextLineageToken, loan.SilverDelta, JsonSerializer.Serialize(loan.Loan));
        }
        string Evidence() => JsonSerializer.Serialize(new
        {
            Snapshots = state.SnapshotStore.ListLatest(), Events = state.Ledger.ListAll(),
            Seed = state.BankLoans.FindByIdempotencyKey("seed"), Debt = state.BankLoans.FindDebtByIdempotencyKey("seed"),
            Loans = Enumerable.Range(1, SnapshotReplayRace.ParticipantCount).Select(i => state.BankLoans.FindByIdempotencyKey(Key(i))).ToArray(),
            Debts = Enumerable.Range(1, SnapshotReplayRace.ParticipantCount).Select(i => state.BankLoans.FindDebtByIdempotencyKey(Key(i))).ToArray()
        });
        string before = Evidence(); long notifications = state.EventNotifications.GetVersion(Actor.UserId);
        var replies = SnapshotReplayRace.Run(i => Call(i), () =>
        {
            if (mode == "revoke") state.LoginSessions.EndUser(Actor.UserId);
            if (mode == "replace") state.LoginSessions.TryCreate(Actor.UserId, Actor.ColonyId!, DateTimeOffset.UtcNow, out _);
        });
        if (mode is "revoke" or "replace")
        {
            Check(replies.All(r => r.Result.ErrorCode == ProtocolErrorCode.SessionUnavailable && r.Id is null && r.SnapshotId is null && r.Lineage is null && r.Delta == 0)
                && Evidence() == before && state.EventNotifications.GetVersion(Actor.UserId) == notifications,
                "Invalidated bank session mutated or disclosed state: " + kind + "/" + mode);
            return;
        }
        var accepted = replies.Where(r => r.Result.Accepted).ToArray();
        Check(accepted.Length == (mode == "same" ? SnapshotReplayRace.ParticipantCount : 1) && accepted.Count(r => r.Result.ErrorCode == ProtocolErrorCode.None) == 1,
            "Concurrent bank replay rejected or duplicated: " + kind + "/" + mode + " " + JsonSerializer.Serialize(replies));
        var winner = accepted.Single(r => r.Result.ErrorCode == ProtocolErrorCode.None);
        Check(winner.SnapshotId == "paid" && winner.Delta == (kind == "borrow" ? 100 : -110)
            && accepted.All(r => r.Id == winner.Id && r.SnapshotId == winner.SnapshotId && r.Lineage == winner.Lineage && r.Delta == winner.Delta && r.Record == winner.Record),
            "Bank responses changed committed result: " + kind);
        int winningAttempt = mode == "same" ? 1 : Array.FindIndex(replies, r => r.Result.Accepted) + 1;
        before = Evidence();
        var replay = Call(winningAttempt).GetAwaiter().GetResult();
        Check(replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.Record == winner.Record && Evidence() == before, "bank replay mutated records");
        // Same key must not authorize changed terms, another business target, another snapshot or another principal.
        var changes = kind == "borrow"
            ? new (string, JsonNode?)[] { ("RequestedLoanId", JsonValue.Create("other")), ("PrincipalSilver", JsonValue.Create(101)), ("DurationDays", JsonValue.Create(2)), ("ExpectedInterestSilver", JsonValue.Create(1)) }
            : new (string, JsonNode?)[] { (kind == "loan" ? "LoanId" : "DebtId", JsonValue.Create("other")), ("SilverPaid", JsonValue.Create(0)) };
        foreach (var change in changes.Append(("ConfirmedSnapshot", JsonSerializer.SerializeToNode(ProtocolDtoMapper.ToMetadataDto(fixture("different"))))))
        {
            var denied = Call(winningAttempt, change.Item1, change.Item2).GetAwaiter().GetResult();
            Check(!denied.Result.Accepted && denied.SnapshotId is null && Evidence() == before, "Changed bank replay accepted: " + kind + "/" + change.Item1);
        }
        var foreign = new ProtocolIdentity("other-user", "other-colony", "before");
        var wrongHeader = Call(winningAttempt, header: Issue(foreign), principal: foreign).GetAwaiter().GetResult();
        Check(wrongHeader.Result.ErrorCode == ProtocolErrorCode.Unauthorized && wrongHeader.Id is null && wrongHeader.SnapshotId is null && Evidence() == before,
            "Bank replay trusted body token over multipart principal: " + kind + "/" + mode + " " + JsonSerializer.Serialize(wrongHeader)
                + ", unchanged=" + (Evidence() == before));
        if (kind == "loan")
        {
            var wrongCreation = new CreateBankLoanWithSnapshotRequest("seed", Actor.UserId, Actor.ColonyId!, "before", token,
                1100, 10000, 100, 1, "loan", 10, ProtocolDtoMapper.ToMetadataDto(package));
            var deniedCreation = SnapshotEndpointTestClient.Invoke<BankLoanResponse>("CreateBankLoanWithSnapshot", state,
                _ => wrongCreation, package.Payload, Actor, token).GetAwaiter().GetResult();
            Check(!deniedCreation.Result.Accepted && deniedCreation.Loan is null && deniedCreation.AppliedSnapshotId is null
                && Evidence() == before, "Repaid loan was replayed as a new borrowing receipt using the repayment snapshot");
        }
        var wrongBody = Call(winningAttempt, "AuthToken", JsonValue.Create("invalid")).GetAwaiter().GetResult();
        Check(!wrongBody.Result.Accepted && wrongBody.Id is null && Evidence() == before, "Bank replay ignored body authentication");
    }

    private sealed record Reply(ProtocolResponse Result, string? Id, string? SnapshotId, string? Lineage, int Delta, string Record);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
