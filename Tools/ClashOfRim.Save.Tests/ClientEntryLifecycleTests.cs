using System.Globalization;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ClientEntryLifecycleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var saved = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            foreach (var kind in new[] { ClientLifecycleEventKind.LoggedIn, ClientLifecycleEventKind.InitialWorldSessionPrepared })
            foreach (bool failure in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Verify(kind, failure);
            }
        }
        finally { CultureInfo.CurrentCulture = saved; }
        // This matrix previously ran inside the broad prepared-payload group. Keep
        // one registration and make the lifecycle boundary independently selectable.
        SnapshotJournalRecoveryPreparationTests.RunLifecycleBoundaries(factory);
    }

    private static void Verify(ClientLifecycleEventKind kind, bool fail)
    {
        var gate = new object(); var recovery = new EconomicModuleTestContext.RecoveryProbe();
        var mutations = new SnapshotMutationCoordinator(gate, recovery);
        var bank = new BankLoanRegistry(); var mercenaries = new MercenaryContractRegistry();
        var now = RaidReadModuleTests.Now.AddHours(2);
        bank.Create("pending", "defender", "defend-colony", "base", 100, 10, 7, 1000, now);
        bank.Create("unrelated", "defender", "other-colony", "base", 100, 10, 7, 1000, now);
        var contract = mercenaries.Create("hire", "defender", "defend-colony", "base", "Shooting", 10, 1, 100, 10, 10, 1000, now);
        var ledger = new InMemoryAuthoritativeEventLedger(); var raid = RaidReadModuleTests.Raid("settled");
        ledger.Append(raid with { Payload = ((RaidEventPayload)raid.Payload) with { FinishedAtUtc = RaidReadModuleTests.Now } });
        var activations = new RaidProtectionActivationRegistry();
        var protection = new RaidProtection(ledger, activations, new(), () => new(), NullLogger.Instance);
        var pending = new PendingConfirmationReconciler(bank, mercenaries, () => new(), mutations);
        var lifecycle = new ClientEntryLifecycle(protection, pending);
        foreach (var ignored in new[] { new ClientLifecycleEvent(ClientLifecycleEventKind.ColonyAbandoned, "defender", "defend-colony", null, null, now),
            new(kind, "", "defend-colony", null, null, now), new(kind, "defender", "", null, null, now) }) lifecycle.Apply(ignored);
        Check(recovery.Replays == 0 && bank.FindByIdempotencyKey("pending") is not null
            && activations.FindActivatedAt("settled", "defender", "defend-colony") is null, "Unrelated entry started recovery or protection.");
        recovery.NeedsPreparation = true;
        recovery.OnPrepare = () =>
        {
            Check(!Monitor.IsEntered(gate) && bank.FindByIdempotencyKey("pending") is not null && mercenaries.Find(contract.ContractId) is not null,
                "Entry cancelled pending work before detached recovery completed.");
            if (fail) throw new IOException("Entry recovery preparation failed.");
        };
        var entry = new ClientLifecycleEvent(kind, "defender", "defend-colony", "base", "session", now);
        if (fail)
        {
            bool failed = false;
            try { lifecycle.Apply(entry); } catch (IOException) { failed = true; }
            Check(failed && bank.FindByIdempotencyKey("pending") is not null && mercenaries.Find(contract.ContractId) is not null,
                "Failed recovery cancelled pending business.");
            fail = false;
        }
        lifecycle.Apply(entry);
        Check(bank.FindByIdempotencyKey("pending") is null && mercenaries.Find(contract.ContractId) is null
            && bank.FindByIdempotencyKey("unrelated") is not null && !Monitor.IsEntered(gate), "Entry did not cancel only its own unconfirmed work.");
        DateTimeOffset? activated = activations.FindActivatedAt("settled", "defender", "defend-colony");
        Check(activated == (kind == ClientLifecycleEventKind.LoggedIn ? now : null), "World preparation started login protection, or login omitted it.");
        lifecycle.Apply(entry with { OccurredAtUtc = now.AddHours(1) });
        Check(activations.FindActivatedAt("settled", "defender", "defend-colony") == activated, "Repeated entry reset protection.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
