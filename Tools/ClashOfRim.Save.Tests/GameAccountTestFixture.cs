using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using EventType = AIRsLight.ClashOfRim.Protocol.ServerEventType;

internal sealed class GameAccountTestFixture : IDisposable
{
    internal static readonly string[] Cases = ["account-password-change", "account-achievements-before-abandon", "account-liabilities-before-abandon", "account-abandon-owner", "account-rebuild-owner", "account-achievements-new-generation", "account-abandon-guest", "account-rebuild-guest"];
    internal readonly GameTradeTestFixture Trade;
    internal GameScenarioTestRuntime Runtime => Trade.Runtime;
    internal readonly InMemoryColonySnapshotIndexStore Snapshots = new();
    internal readonly PlayerRegistry Players = new();
    internal readonly WorldConfigurationRegistry World = GameWorldTestFixture.ReadyRegistry();
    internal readonly BankLoanRegistry Loans = new();
    internal readonly MercenaryContractRegistry Mercenaries = new();
    internal readonly OfflineAccountRegistry Credentials = new();
    internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
    internal readonly AchievementRegistry Achievements = new();
    internal readonly AccountLifecycleAuthority Authority;
    internal readonly AccountColonyScenario Colonies;
    internal readonly AccountLifecycleScenario Scenario;
    internal readonly Dictionary<string, string> Colony = new() { ["A"] = "old-A", ["B"] = "old-B" };
    internal readonly Dictionary<string, bool> Active = new() { ["A"] = true, ["B"] = true };
    internal string Address = "http://first-address", Fault = "none";
    internal int Confirmed, Cancelled, Entries, PlayerReads;
    private readonly SaveSnapshotPackage template;
    private readonly List<string> contracts = [];
    private readonly Dictionary<string, int> countdown = [], opened = [], menuDelay = [], playerDelay = [], versions = new() { ["A"] = 0, ["B"] = 0 }, epochs = new() { ["A"] = 0, ["B"] = 0 };
    private readonly Dictionary<string, (string User, string Colony)> achievementTargets = [];
    private readonly HashSet<string> entering = [], seenEntry = [];
    private string message = "", alliance = "";
    private bool allied, readMessage;
    private int shopStock, shopPrice = 10, chatReads;
    private GameAccountTestFixture(GameTradeTestFixture trade, SaveSnapshotPackage template)
    {
        Trade = trade; this.template = template; World.PromoteAdministrator("rimtest-A");
        foreach (string client in new[] { "A", "B" })
        {
            Check(Credentials.Authenticate(User(client), "local-test-password", DateTimeOffset.UtcNow, true).Accepted, "Seed credentials failed.");
            SeedColony(client); Trade.Stock[client]["Silver"] = 1000;
        }
        var run = Runtime.Run; var polling = Runtime.Polling; var colony = new ColonyScenarioFixture(run, polling);
        var economy = new EconomicScenarioFixture(run, polling, colony);
        var achievements = new AchievementScenario(run, polling, trade.Trades, Achievements, Snapshots, Players);
        var world = new WorldScenario(run, polling, new(), World, new Distance(), _ => throw new InvalidOperationException("Unexpected persistence probe."));
        var entry = Runtime.CreateEntry(() => Address, world: world);
        var chat = new ChatScenario(run, polling); var players = new PlayerScenarioFixture(run, polling); var shopActions = new ShopScenarioFixture(run, polling);
        Authority = new(Snapshots, Players, World, Loans, Mercenaries, Credentials, Ledger);
        Colonies = new(run, polling, colony, economy, achievements, entry, chat, players, shopActions, Authority, () => Address);
        var mercenaries = new MercenaryScenario(run, polling, colony, economy, Mercenaries, new(), () => (1, 1f));
        Scenario = new(run, polling, colony, economy, mercenaries, new(run, polling, Credentials), achievements, chat,
            new(run, polling, colony, economy, shopActions), new(run, polling, players), trade.Trades, trade.Actions, Colonies);
        Runtime.Script = Reply;
    }
    internal static async Task<GameAccountTestFixture> Create(SaveSnapshotPackage template)
        => new(await GameTradeTestFixture.Create(plannedCases: Cases), template);
    private static string User(string client) => "rimtest-" + client;
    private static int Tile(string client) => client == "A" ? 10 : 20;
    private void SeedColony(string client)
    {
        string user = User(client), colony = Colony[client]; var identity = new SnapshotIdentity(user, colony, "snapshot-" + colony);
        if (Fault != "rebuilt-snapshot") Snapshots.StoreLatest(new(identity, template.Envelope with { Identity = identity }, template.Index, DateTimeOffset.UnixEpoch));
        Players.Record(user, colony, identity.SnapshotId, DateTimeOffset.UnixEpoch);
        string siteColony = Fault == "wrong-site-generation" ? "unrelated-colony" : colony;
        World.RegisterPlayerColonySites(user, siteColony, [new(user, siteColony, "world", "map-" + colony, Tile(client), "Home")]);
    }
    private object Shop() => List(new { data = new { listingId = "shared-listing", stockCount = shopStock, priceSilver = shopPrice, item = new { defName = "Steel" } }, reference = new { handle = "shop-current" } });
    private object Bank(string client) => new { minLoanSilver = 10, minDurationDays = 3, mercenaryMinDurationDays = 2,
        activeLoan = Loans.GetActive(User(client), Colony[client]), openDebts = Loans.GetOpenDebts(User(client), Colony[client]) };
    private void Borrow(string client) => Loans.Create("loan-" + Colony[client], User(client), Colony[client], "source", 10, 1, 3, 0, DateTimeOffset.UnixEpoch);
    private string Hire()
    {
        var contract = Mercenaries.Create("hire-" + contracts.Count, User("B"), Colony["B"], "source", "Mining", 7, 2, 5, 2, 3, 0, DateTimeOffset.UnixEpoch);
        contracts.Add(contract.ContractId); Trade.Stock["B"]["Silver"] -= 5; return contract.ContractId;
    }
    private void AddTrade(string id, bool memo = false) => Ledger.Append(AuthoritativeEventFactory.Create(EventType.Trade,
        new(User(memo ? "B" : "A"), Colony[memo ? "B" : "A"]), new(User(memo ? "A" : "B"), Colony[memo ? "A" : "B"]), id + (memo ? "-memo" : ""), false,
        new TradeEventPayload(id, memo ? TradeStage.AcceptedMemo : TradeStage.MarketOrder, [], [], 0), DateTimeOffset.UnixEpoch) with { EventId = id + (memo ? "-memo" : "") });
    internal AccountLifecyclePreparation SeedPreparation()
    {
        Borrow("A"); Borrow("B"); Hire();
        Mercenaries.Create("extra-A", User("A"), Colony["A"], "source", "Mining", 7, 2, 5, 2, 3, 0, DateTimeOffset.UnixEpoch);
        var debt = Loans.CreateDebt("death", User("B"), Colony["B"], "source", 3, "Fine", "Mercenary:Death", "contract", 0, DateTimeOffset.UnixEpoch);
        AddTrade("prepared-order"); AddTrade("prepared-order", true); shopStock = 2; message = "RimTest private exchange"; allied = true;
        Achievements.Record(User("A"), Colony["A"], "history", new("trades_10", "history", 10), DateTimeOffset.UnixEpoch);
        return new("prepared-order", debt.DebtId, Wire.Element(Shop()));
    }
    internal void AbandonState(string client)
    {
        string user = User(client), colony = Colony[client], survivor = client == "A" ? "B" : "A";
        Active[client] = false; allied = false;
        if (Fault != "snapshot") Snapshots.RemoveLatest(user, colony);
        if (Fault != "tombstone") Players.MarkDeleted(user, colony, "source", DateTimeOffset.UnixEpoch);
        if (Fault != "site") World.RemovePlayerColonySites(user, colony);
        if (Fault != "loan") Loans.RemoveForColony(user, colony);
        if (Fault == "debt") Loans.CreateDebt("leftover", user, colony, "source", 3, "Fine", "old", "old", 0, DateTimeOffset.UnixEpoch);
        if (Fault != "contract") Mercenaries.CloseForColony(user, colony);
        foreach (var item in Ledger.ListAll().Where(e => e.Status != ServerEventStatus.AppliedToSnapshot))
            if (Fault != (item.Payload is TradeEventPayload { Stage: TradeStage.AcceptedMemo } ? "memo" : "order")) Ledger.ChangeStatus(item.EventId, ServerEventStatus.Cancelled);
        if (Fault == "credentials") Credentials.ResetPassword(user, "lost-password", DateTimeOffset.UtcNow, out _);
        if (Fault == "administrator") World.RevokeAdministrator("rimtest-A");
        if (Fault == "history") Achievements.Record(user, colony, "unexpected", new("extra", "unexpected", 1), DateTimeOffset.UnixEpoch);
        if (Fault == "survivor-snapshot") Snapshots.RemoveLatest(User(survivor), Colony[survivor]);
        if (Fault == "survivor-stock") Trade.Stock[survivor]["Silver"]++;
        if (Fault == "survivor-debt") Loans.RemoveForColony(User(survivor), Colony[survivor]);
    }
    private object? Reply(string client, string method, JsonElement args)
    {
        switch (method)
        {
            case "clash.state": return new { colonyId = Colony[client], currentSnapshotId = "snapshot-" + Colony[client], isInActiveMultiplayerSession = Active[client],
                isAdministrator = Fault == "rebuilt-role" && seenEntry.Contains(client) ? client != "A" : client == "A", unreadPrivateChatCount = readMessage ? 0 : 1 };
            case "clash.inventory": return new { maps = new[] { new { tileId = Tile(client), items = Trade.Stock[client], colonists = Enumerable.Range(0, 3).Select(i => new { reference = new { handle = client + "-pawn-" + i } }).ToArray() } } };
            case "clash.bank": return Bank(client);
            case "clash.bank.quests": return new { loans = Fault == "old-quest" ? new[] { "old-loan" } : [], debts = Array.Empty<object>() };
            case "clash.mercenaries": return List(contracts.Select(Mercenaries.Find).Where(c => c is not null && c.ColonyId == Colony[client]).Select(c => (object)new
            { contractId = c!.ContractId, completed = c.Status == MercenaryContractRegistry.StatusCompleted, spawned = true, actualSkill = 7, priceSilver = 5, pawn = new { handle = c.ContractId } }).ToArray());
            case "clash.shop.template": return new { reference = new { handle = "steel-template" } };
            case "clash.abandon.open": opened[client] = opened.GetValueOrDefault(client) + 1; countdown[client] = 0; return new { };
            case "clash.abandon": return new { ready = Fault == "countdown" || opened[client] % 2 == 0 && countdown[client]++ > 0 };
            case "clash.abandon.choose":
                if (!args.Bool("confirm")) { Cancelled++; if (Fault == "cancel-effect") Snapshots.RemoveLatest(User(client), Colony[client]); return new { }; }
                Confirmed++; AbandonState(client); menuDelay[client] = 1;
                if (Fault == "abandon-response") throw new IOException("Lost abandonment response.");
                return new { };
            case "game.state":
                bool delayed = menuDelay.GetValueOrDefault(client) > 0; menuDelay[client] = 0;
                return new { startupReady = true, hasGame = Active[client] || delayed, playing = Active[client] || delayed, epoch = "epoch-" + epochs[client], playerHasControl = Active[client] && !entering.Contains(client), mapCount = 1 };
            case "pages.list": return entering.Contains(client) ? List(new { reference = new { handle = "site-page", type = "RimWorld.Page_SelectStartingSite" } }) : List();
            case "setup.sites": Check(args.Int("nearTileId") == Tile(client == "A" ? "B" : "A"), "Rebuild lost surviving neighbour tile."); return List(new { tileId = Tile(client), layerId = 0 });
            case "page.next": entering.Remove(client); return new { };
            case "game.load": epochs[client]++; return new { };
            case "clash.fixture.bankCash": Check(args.Bool("acknowledgeFixture"), "Bank fixture unacknowledged."); Trade.Stock[client]["Silver"] = args.Int("amount"); return new { };
            case "clash.fixture.mercenaryDeath":
                string contract = args.String("contractId"); Check(args.Bool("acknowledgeFixture"), "Mercenary fixture unacknowledged.");
                Mercenaries.Complete(contract, User(client), Colony[client]); Loans.CreateDebt("death-" + contract, User(client), Colony[client], "source", 3, "Fine", "Mercenary:Death", contract, 0, DateTimeOffset.UnixEpoch); return new { };
            case "clash.events": return alliance == "" ? List() : List(new { data = new { eventId = alliance, eventType = "allianceRequest", actor = new { userId = User("A"), colonyId = Colony["A"] }, target = new { userId = User("B"), colonyId = Colony["B"] } } });
            case "clash.achievement.version": return new { version = versions[client] };
            case "clash.records":
                switch (args.String("kind"))
                {
                    case "shop": return Shop();
                    case "chat": if (Fault == "missing-history" && ++chatReads > 1) throw new IOException("No historical message."); return Fault == "missing-history" ? List() : List(new { data = new { text = message, channel = "Private", targetUserId = User("B") } });
                    case "players":
                        PlayerReads++; string other = client == "A" ? "B" : "A";
                        if (playerDelay.GetValueOrDefault(client) > 0) { playerDelay[client]--; return List(Player(other, "obsolete", "Ally")); }
                        return Active[other] ? List(Player(other, Colony[other], allied ? "Ally" : "Neutral")) : List();
                    case "achievements":
                        var target = achievementTargets[client];
                        if (Fault == "deleted-achievement" && Players.IsDeleted(target.User, target.Colony)) return List(new { data = new { achievementId = "trades_10" } });
                        return List(VisibleAchievements().Where(a => a.UserId == target.User && a.ColonyId == target.Colony).Select(a => (object)new { data = new { achievementId = a.AchievementId, value = a.Value } }).ToArray());
                    case "leaderboards": return List(VisibleAchievements().GroupBy(a => a.AchievementId).Select(g => (object)new { data = new { achievementId = g.Key,
                        entries = g.OrderByDescending(a => a.Value).ThenBy(a => a.UserId, StringComparer.Ordinal).ThenBy(a => a.ColonyId, StringComparer.Ordinal).Take(10)
                            .Select(a => new { userId = a.UserId, colonyId = a.ColonyId, value = a.Value, sourceSnapshotId = a.SourceSnapshotId }).ToArray() } }).ToArray());
                }
                break;
            case "clash.invoke":
                var input = args.GetProperty("arguments");
                switch (args.String("action"))
                {
                    case "StartChangeOfflinePassword": Credentials.ChangePassword(User(client), input.String("currentPassword"), input.String("newPassword"), DateTimeOffset.UtcNow, out _); return new { };
                    case "StartManualSnapshotUpload": if (Fault == "upload-replay") Achievements.Record(User(client), Colony[client], "replayed", new("extra", "replayed", 1), DateTimeOffset.UnixEpoch); return new { };
                    case "StartSendChatMessage": message = input.String("rawText")[11..]; return new { };
                    case "MarkPrivateChatRead": readMessage = true; return new { };
                    case "StartRefreshChatMessages": case "StartRefreshServerShop": case "StartRefreshBankStatus": return new { };
                    case "StartRefreshPlayers": playerDelay[client] = 1; return new { };
                    case "StartUpsertServerShopListing": shopStock = input.Int("stockCount"); return new { };
                    case "StartPurchaseServerShopListing": shopStock--; Trade.Stock[client]["Silver"] -= shopPrice; Trade.Stock[client]["Steel"] += 5; return new { };
                    case "StartCreateDiplomacyEvent": alliance = "alliance"; return new { };
                    case "HandleEventLetterAction": allied = true; return new { };
                    case "StartCreateBankLoan": Borrow(client); return new { };
                    case "StartHireMercenary": Hire(); return new { };
                    case "StartRefreshAchievements": versions[client]++; achievementTargets[client] = (input.OptionalString("targetUserId") ?? User(client), input.OptionalString("targetColonyId") ?? Colony[client]); return new { };
                    case "StartMainMenuServerEntryFlow":
                        Check(!input.Bool("createAccountIfMissing") && input.String("serverBaseUrl") == Address && input.String("userId") == User(client), "Rebuild registered another account or used stale server address.");
                        Entries++; Colony[client] = Fault == "same-colony" ? Colony[client] : "rebuilt-" + client; Active[client] = true;
                        entering.Add(client); seenEntry.Add(client); SeedColony(client); Trade.Stock[client]["Steel"] = Trade.Stock[client]["WoodLog"] = 100;
                        if (Fault == "rebuilt-loan") Borrow(client);
                        if (Fault == "rebuilt-achievement") Achievements.Record(User(client), Colony[client], "wrong", new("trades_10", "wrong", 10), DateTimeOffset.UnixEpoch);
                        if (Fault == "changed-shop") shopPrice++;
                        if (Fault == "entry-response") throw new IOException("Lost entry response.");
                        return new { returnValue = true };
                }
                break;
        }
        int before = Trade.Creates;
        var reply = Trade.Reply(client, method, args);
        if (Trade.Creates != before) AddTrade("order-" + Trade.Creates);
        if (method == "clash.invoke" && args.String("action") == "StartAcceptTradeOrder") AddTrade("order-" + Trade.Creates, true);
        if (method == "clash.invoke" && args.String("action") == "StartFulfillTradeOrderByDropPod")
        {
            Ledger.ChangeStatus("order-" + Trade.Creates, ServerEventStatus.AppliedToSnapshot);
            foreach (string party in new[] { "A", "B" }) Achievements.RecordCumulativeMetricAchievements(User(party), Colony[party], "trade-" + Trade.Deliveries, "trade-" + Trade.Deliveries,
                new Dictionary<string, long> { [AchievementRegistry.MetricCompletedTradeCount] = 1 }, null, DateTimeOffset.UnixEpoch);
        }
        return reply;
    }
    private IEnumerable<AchievementAggregateRecord> VisibleAchievements() => Achievements.ListAggregates().Where(a => Snapshots.GetLatest(a.UserId, a.ColonyId) is not null && !Players.IsDeleted(a.UserId, a.ColonyId));
    private static object Player(string client, string colony, string relation) => new { reference = new { handle = "player-" + client }, data = new { userId = User(client), colonyId = colony, relationKind = relation } };
    private sealed class Distance : IWorldTileDistanceCalculator
    { public int? TryCalculateDistance(WorldTileGeometryDistanceSource? geometry, WorldTileRef fromTile, WorldTileRef toTile, int overhead) => 2; }
    private static object List(params object[] items) => new { items };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public void Dispose() => Trade.Dispose();
}
