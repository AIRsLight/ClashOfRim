using System.Globalization;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class MercenaryModuleTests
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
                Quotes(factory);
                foreach (bool guard in new[] { false, true })
                {
                    HireAndReplay(factory, guard);
                    foreach (int phase in new[] { 1, 2 }) RevokedDuringRecovery(factory, guard, phase);
                }
                foreach (string kind in new[] { "Death", "ShuttleDestroyed", "HarmfulSurgery", "HostileBehavior", "OvertimeService", "Completed" })
                    Incident(factory, kind);
            }
            foreach (string change in new[] { "disabled", "price", "fine", "duration", "limit" }) ChangedTerms(factory, false, change);
            foreach (string change in new[] { "disabled", "pvp", "guards", "guard-price", "ratio" }) ChangedTerms(factory, true, change);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static EconomicModuleTestContext Fixture(Func<string, SaveSnapshotPackage> factory) => new(factory)
    { Configuration = MercenaryCommitCrashTests.Configuration() };

    private static void Quotes(Func<string, SaveSnapshotPackage> factory)
    {
        var f = Fixture(factory);
        foreach (string invalid in new[] { "skill", "level", "duration", "token" })
        {
            var result = f.Mercenaries.QuoteMercenary(new("userA", "colonyA", "before", invalid == "token" ? "forged" : f.Token,
                1000, invalid == "skill" ? "unknown" : "Mining", invalid == "level" ? 3 : 7, invalid == "duration" ? -1 : 7));
            Check(!result.Result.Accepted && f.Recovery.Replays == 0, "Invalid quote triggered recovery: " + invalid);
        }
        // The new configuration is installed during recovery; both quote paths must read it afterwards.
        f.Recovery.OnReplay = () => f.Configuration = new(pvpEnabled: true, mercenaryApprenticeDailySilver: 100,
            mercenaryHarmfulSurgeryFineSilver: 1234, mercenaryApprenticeDeathFineSilver: 2345,
            mercenaryDurationMultiplierCurve: new[] { new BankInterestDurationMultiplierPointDto(1, 1), new BankInterestDurationMultiplierPointDto(7, 1.25f) });
        var quote = f.Mercenaries.QuoteMercenary(new("userA", "colonyA", "before", f.Token, 1000, "Mining", 7, 7));
        Check(quote.Result.Accepted && quote.PriceSilver == 875 && quote.HarmfulSurgeryFineSilver == 1234 && quote.DeathFineSilver == 2345,
            "Mercenary quote did not use the recovered configuration.");
        foreach (string tier in new[] { "Apprentice", "unknown", "" })
        {
            var guard = f.Mercenaries.QuoteMercenaryGuard(new("userA", "colonyA", "before", f.Token, 1000, tier));
            Check(guard.Result.Accepted && guard.Tier == "Apprentice" && guard.PriceSilver == 600 && guard.PointRatio == 0.35f,
                "Guard default-tier compatibility changed.");
        }
        var skilled = f.Mercenaries.QuoteMercenaryGuard(new("userA", "colonyA", "before", f.Token, 1000, "sKiLlEd"));
        Check(skilled.Result.Accepted && skilled.Tier == "Skilled", "Guard normalization depended on current culture.");
        Check(f.Recovery.Commits == 0 && f.MercenaryContracts.CountOpenForColony("userA", "colonyA") == 0
            && f.MercenaryGuards.FindActiveForColony("userA", "colonyA") is null, "Quote created a contract or snapshot.");
    }

    private static void HireAndReplay(Func<string, SaveSnapshotPackage> factory, bool guard)
    {
        var f = Fixture(factory);
        foreach (string? token in new[] { null, "forged", f.Issue("other", "other-colony") })
            Check(!Hire(f, guard, token).Result.Accepted && f.Recovery.Replays == 0, "Unauthorized hire triggered recovery.");
        Check(!Hire(f, guard, f.Token, bodyToken: "forged").Result.Accepted && f.Recovery.Replays == 0,
            "Hire accepted a valid header with an invalid body credential.");
        var first = Hire(f, guard, f.Token);
        Check(first.Result.Accepted && first.ContractId == "contract" && first.SnapshotId == "paid"
            && !string.IsNullOrWhiteSpace(first.Token) && f.Recovery.Commits == 1, "Independent hire failed: " + first.Result.Message);
        if (guard) f.MercenaryGuards.ConsumeForRaid("userA", "colonyA", "raid", "contract", DateTimeOffset.UtcNow);
        else f.MercenaryContracts.Complete("contract", "userA", "colonyA");
        f.Configuration = new(pvpEnabled: false, mercenariesEnabled: false, mercenaryGuardsEnabled: false);
        var replay = Hire(f, guard, f.Token);
        Check(replay.Result.Accepted && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && replay.ContractId == first.ContractId && replay.SnapshotId == first.SnapshotId && replay.Token == first.Token
            && f.Recovery.Commits == 1, "Replay revalidated disabled features or repeated settlement.");
        Check(guard ? f.MercenaryGuards.FindByIdempotencyKey("hire")!.Status == MercenaryGuardContractRegistry.StatusConsumed
            : f.MercenaryContracts.Find("contract")!.Status == MercenaryContractRegistry.StatusCompleted,
            "Replay reopened a terminal contract.");
        Check(!Hire(f, guard, f.Token, price: 999).Result.Accepted && f.Recovery.Commits == 1, "Changed price borrowed a receipt.");
        f.Tokens.RevokeForUser("userA");
        Check(!Hire(f, guard, f.Token).Result.Accepted && f.Recovery.Commits == 1, "Receipt replay bypassed revocation.");
    }

    private static void RevokedDuringRecovery(Func<string, SaveSnapshotPackage> factory, bool guard, int phase)
    {
        var f = Fixture(factory); int calls = 0;
        f.Recovery.OnReplay = () => { if (++calls == phase) f.Tokens.RevokeForUser("userA"); };
        Check(!Hire(f, guard, f.Token).Result.Accepted && calls == phase, "Hire ignored revocation in phase " + phase);
        CheckUncommitted(f);
    }

    private static void ChangedTerms(Func<string, SaveSnapshotPackage> factory, bool guard, string change)
    {
        var f = Fixture(factory); int calls = 0;
        f.Recovery.OnReplay = () =>
        {
            if (++calls != 2) return;
            f.Configuration = new(pvpEnabled: change != "pvp", mercenariesEnabled: change != "disabled",
                mercenaryGuardsEnabled: change != "guards", maxActiveMercenariesPerColony: change == "limit" ? 0 : 1,
                mercenaryApprenticeDailySilver: change == "price" ? 81 : 80,
                mercenaryHarmfulSurgeryFineSilver: change == "fine" ? 1001 : 1000,
                mercenaryMinDurationDays: change == "duration" ? 8 : 1,
                mercenaryGuardApprenticeSilver: change == "guard-price" ? 601 : 600,
                mercenaryGuardApprenticePointsRatio: change == "ratio" ? 0.36f : 0.35f);
        };
        Check(!Hire(f, guard, f.Token).Result.Accepted && calls == 2, "Hire ignored changed configuration: " + change);
        CheckUncommitted(f);
    }

    private static void Incident(Func<string, SaveSnapshotPackage> factory, string kind)
    {
        var f = Fixture(factory);
        f.MercenaryContracts.ActivateWithSnapshot("hire", "userA", "colonyA", "contract", "before", "Mining", 7, 7,
            560, 1000, 1500, 1000, DateTimeOffset.UtcNow);
        MercenaryIncidentResponse Report(string key, string reason) => f.Mercenaries.ReportMercenaryIncident(
            new(key, "userA", "colonyA", "before", f.Token, 1000, "contract", reason));
        Check(!Report("", kind).Result.Accepted && !Report("bad", "unknown").Result.Accepted && f.Recovery.Replays == 0,
            "Invalid incident triggered recovery.");
        var first = Report("incident", kind);
        Check(first.Result.Accepted, "Incident report failed: " + kind);
        var replay = Report("incident", kind);
        if (kind == "Completed")
        {
            Check(first.Debt is null && replay.Result.Accepted && f.MercenaryContracts.Find("contract")!.Status == MercenaryContractRegistry.StatusCompleted
                && f.Ledger.ListAll().Count == 0, "Completion was not idempotent or created a fine.");
            return;
        }
        int expected = kind switch { "Death" => 1500, "ShuttleDestroyed" => 6000, "OvertimeService" => 240, _ => 1000 };
        Check(first.Debt!.AmountSilver == expected && replay.Debt!.DebtId == first.Debt.DebtId
            && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest, "Incorrect or duplicate incident fine.");
        f.MercenaryIncidents.Recover();
        Check(f.Ledger.ListAll().Count == 0 && f.MercenaryContracts.FindActive("contract") is not null,
            "Unconfirmed fine produced side effects.");
        Check(!Report("incident", "Completed").Result.Accepted, "Incident key accepted different terms.");
        f.Loans.ConfirmPendingForSnapshot("userA", "colonyA", "confirmed", 1000, DateTimeOffset.UtcNow);
        f.MercenaryIncidents.Recover(); f.MercenaryIncidents.Recover();
        bool closes = kind is "Death" or "ShuttleDestroyed";
        Check(f.Loans.FindDebtByIdempotencyKey("incident")!.MercenaryIncidentFinalized
            && (f.MercenaryContracts.FindActive("contract") is null) == closes && f.Ledger.ListAll().Count == 1,
            "Confirmed fine did not finalize exactly once.");
        var notification = f.Ledger.ListAll().Single();
        Check(notification.Target == new EventParty("userA", "colonyA")
            && notification.Payload is ServerNotificationEventPayload { Severity: ServerNotificationSeverity.Warning } payload
            && payload.NotificationId == "bank-fine-created:" + first.Debt.DebtId,
            "Module recovery published the wrong notification.");
    }

    private static MercenaryCommitCrashTests.Reply Hire(EconomicModuleTestContext f, bool guard, string? token,
        string? bodyToken = null, int? price = null)
    {
        var metadata = ProtocolDtoMapper.ToMetadataDto(f.Package);
        if (guard)
        {
            var r = f.MercenaryHiring.HireMercenaryGuardWithSnapshot(new("hire", "userA", "colonyA", "before", bodyToken ?? f.Token,
                1000, "contract", "Apprentice", price ?? 600, 0.35f, metadata), f.Package.Payload, token);
            return new(r.Result, r.Contract?.ContractId, r.AppliedSnapshotId, r.NextLineageToken);
        }
        var hire = f.MercenaryHiring.HireMercenaryWithSnapshot(new("hire", "userA", "colonyA", "before", bodyToken ?? f.Token,
            1000, "Mining", 7, 7, "contract", price ?? 560, 1000, 1500, metadata), f.Package.Payload, token);
        return new(hire.Result, hire.Contract?.ContractId, hire.AppliedSnapshotId, hire.NextLineageToken);
    }

    private static void CheckUncommitted(EconomicModuleTestContext f) => Check(f.Recovery.Commits == 0
        && f.MercenaryContracts.FindByIdempotencyKey("hire") is null && f.MercenaryGuards.FindByIdempotencyKey("hire") is null
        && f.Snapshots.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before", "Rejected hire changed domain or snapshot state.");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
