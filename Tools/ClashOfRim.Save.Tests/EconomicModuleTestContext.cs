using AIRsLight.ClashOfRim.Network.Modules.Trades;
using AIRsLight.ClashOfRim.Network.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Network.Modules.Shop;
using Microsoft.Extensions.Logging.Abstractions;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Bank;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Modules.Gifts;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Transfers;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;

// Composes actual domain modules without a network host or its aggregate state.
internal sealed class EconomicModuleTestContext
{
    internal readonly InMemoryColonySnapshotIndexStore Snapshots = new();
    internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
    internal readonly BankLoanRegistry Loans = new();
    internal readonly LoginSessionRegistry Sessions = new();
    internal readonly AuthTokenRegistry Tokens = new();
    internal readonly RecoveryProbe Recovery = new();
    internal readonly DiplomacyRelationRegistry Relations = new();
    internal readonly OnlinePresenceRegistry Presence = new();
    internal readonly EventNotificationHub Notifications = new();
    internal readonly PawnPackageRegistry Pawns = new();
    internal readonly ThingPackageRegistry Things = new();
    internal readonly object Gate = new();
    internal readonly RaidProtectionProbe Raids;
    internal ClashOfRimServerConfiguration Configuration = new(bankLoansEnabled: true, bankMinLoanSilver: 1,
        bankMinDurationDays: 1, bankBaseAnnualInterestRate: 0);
    internal readonly BankRequestAccess Access;
    internal readonly BankService Bank;
    internal readonly MercenaryContractRegistry MercenaryContracts = new();
    internal readonly MercenaryGuardContractRegistry MercenaryGuards = new();
    internal readonly MercenaryService Mercenaries;
    internal readonly MercenaryHiring MercenaryHiring;
    internal readonly MercenaryIncidentRecovery MercenaryIncidents;
    internal readonly GiftService Gifts;
    internal readonly TradeService Trades;
    internal readonly ServerShopRegistry ShopRegistry = new();
    internal readonly ShopService Shop;
    internal readonly TradeMarket Market;
    internal readonly TradeLifecycle TradeLifecycle;
    internal readonly TradeDelivery TradeDelivery;
    internal readonly AdminBaselineRegistry Baseline = new();
    internal readonly WorldConfigurationRegistry World = new(WorldConfigurationExtensionService.Empty);
    internal readonly ItemTransferPackages Packages;
    internal readonly SaveSnapshotPackage Package;
    internal readonly string Token;

    internal EconomicModuleTestContext(Func<string, SaveSnapshotPackage> factory)
    {
        var mercenaries = MercenaryContracts;
        var mutations = new SnapshotMutationCoordinator(Gate, Recovery);
        var colonies = new ColonyAccessAuthorization(Tokens, Sessions, Relations,
            new RaidPreparationRegistry(), Ledger, () => Configuration.PvpEnabled);
        var pending = new PendingConfirmationReconciler(Loans, mercenaries, () => Configuration, Gate);
        Access = new BankRequestAccess(colonies, mutations, Snapshots, Loans, pending);
        var principals = new SnapshotPrincipalAuthorization(Tokens, Sessions);
        var requests = new SnapshotRequestAuthorization(principals, colonies, mutations);
        var transactions = new SnapshotTransactions(Snapshots, SnapshotUploadPolicy.AllowAnyVersion,
            new ServerPluginRegistry(), new CompatibilityBaselineRegistry());
        var commits = new SnapshotCommitService();
        Bank = new BankService(Loans, mercenaries, Snapshots, commits, Access, requests, transactions, Recovery, () => Configuration);
        Mercenaries = new MercenaryService(mercenaries, MercenaryGuards, Loans, Access, Bank, () => Configuration);
        MercenaryHiring = new MercenaryHiring(mercenaries, MercenaryGuards, Access, Bank, requests, transactions,
            new MercenarySnapshotCommitter(commits, Snapshots, Loans, mercenaries, MercenaryGuards, Recovery), () => Configuration);
        MercenaryIncidents = new MercenaryIncidentRecovery(Loans, mercenaries, Ledger, Presence, Notifications);
        Raids = new RaidProtectionProbe(Gate);
        Packages = new ItemTransferPackages(Pawns, Things);
        Gifts = new GiftService(Ledger, Snapshots, Loans, mercenaries, commits, requests, transactions, pending,
            Packages, new DeliveryTargetResolver(Snapshots), Presence, Notifications, Relations, Raids, Recovery, () => Configuration);
        var plugins = new ServerPluginRegistry();
        var compatibility = new CompatibilityBaselineRegistry();
        TradeDelivery = new TradeDelivery(Snapshots, Presence, Ledger, plugins, compatibility, World,
            new StraightLineWorldTileDistanceCalculator(), () => Configuration);
        TradeLifecycle = new TradeLifecycle(Ledger, Snapshots, mutations, Gate, TradeDelivery, Baseline,
            Presence, Notifications, NullLogger.Instance, () => Configuration);
        var tradeCommits = new TradeSnapshotCommitter(commits, Snapshots, Loans, Ledger, mercenaries, Notifications, Recovery, Recovery);
        Trades = new TradeService(Ledger, requests, colonies, mutations, transactions, pending, Packages,
            tradeCommits, TradeLifecycle, TradeDelivery, Baseline, plugins, compatibility, Presence,
            Notifications, NullLogger.Instance, () => Configuration);
        Market = new TradeMarket(Ledger, requests, TradeLifecycle, TradeDelivery, () => Configuration);
        Shop = new ShopService(ShopRegistry, requests, new AdministratorAuthorization(Tokens, Sessions, World), mutations, transactions, pending,
            new ShopSnapshotCommitter(commits, Snapshots, Loans, ShopRegistry, mercenaries, Recovery), Baseline, plugins, compatibility,
            NullLogger.Instance, () => Configuration);
        Package = MercenaryCommitCrashTests.PrepareEndpoint(Snapshots, factory, new("userA", "colonyA", "before"), "paid");
        Token = Issue("userA", "colonyA");
    }

    internal string Issue(string userId, string colonyId)
    {
        var now = DateTimeOffset.UtcNow;
        Sessions.TryCreate(userId, colonyId, now, out string session);
        return Tokens.Issue(userId, userId, colonyId, session, now);
    }

    internal sealed class RecoveryProbe : ISnapshotRecovery, ISnapshotRecoveryPreparation, ISnapshotCommitEffects, IEventAchievementRecorder
    {
        internal int Replays, Commits;
        internal readonly List<string> AchievementEvents = new();
        public void Record(AuthoritativeEvent item, DateTimeOffset nowUtc) => AchievementEvents.Add(item.EventId);
        internal Action? OnReplay;
        public void RecoverPrerequisites() { }
        public ISnapshotRecoveryPreparation CapturePreparation() => this;
        public void Replay() { Replays++; OnReplay?.Invoke(); }
        public bool NeedsPreparation => false;
        public bool IsCurrent => true;
        public void Prepare() { }
        public bool TryInstall() => true;
        public void AfterCommit(LatestSnapshotRecord snapshot, string? uploadKind) => Commits++;
    }

    internal sealed class RaidProtectionProbe(object gate) : IRaidDeliveryProtection
    {
        internal int Reconciliations;
        internal Action? OnReconcile;
        internal AuthoritativeEvent? BlockingRaid;
        internal DateTimeOffset? CooldownUntil;
        public void Reconcile(DateTimeOffset nowUtc)
        {
            if (Monitor.IsEntered(gate)) throw new Exception("Raid recovery ran inside gift mutation gate.");
            Reconciliations++;
            OnReconcile?.Invoke();
        }
        public AuthoritativeEvent? FindBlockingRaid(string userId, string? colonyId) => BlockingRaid;
        public DateTimeOffset? CurrentCooldownUntil(string userId, string? colonyId, DateTimeOffset nowUtc) => CooldownUntil;
    }
}
