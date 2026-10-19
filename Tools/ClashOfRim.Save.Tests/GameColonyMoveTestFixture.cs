using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using ClashOfRim.GameTests;
using RimTest.Protocol;

// Script observations, not the product's relocation or flight implementation.
// Both real scenario modules use this same connection and authoritative world registry.
internal sealed class GameColonyMoveTestFixture : IDisposable
{
    internal readonly GameScenarioTestRuntime Runtime;
    internal readonly WorldConfigurationRegistry World = GameWorldTestFixture.ReadyRegistry();
    internal readonly ColonyMoveScenarioFixture Moves;
    internal readonly RelocationScenario Relocation;
    internal readonly GravshipScenario Gravship;
    internal readonly string Client, Kind, Label;
    internal readonly int Tile;
    internal string Fault = "none";
    internal bool Delayed, ExistingMarker, Ticking, Moved, Reloaded;
    internal int CommitReads, ReloadReads, RelocationReads, Starts, Stops, Repayments;
    private bool loan, loanLanded, enemy, removedEnemy, fixtureReady, flying;
    private int silver, steel, flightPhase, phaseReads;
    private bool marker, dialog;
    private string? saveName;
    internal string Colony => "rebuilt-colony-" + Client;
    internal string Snapshot => "before-" + Client;
    private string Loan => "loan-" + Client;
    internal string[] Pawns => ["pawn-z-" + Client, "pawn-a-" + Client];
    private object Dlc => new { pawns = new[] { new { id = Pawns[0], genes = new[] { "gene-one", "gene-two" },
        ideology = new { culture = "文化-İ", memes = new[] { "Meme" }, certainty = 0.75 }, royalty = new { level = 2 },
        hediffs = new[] { new { def = "complex-condition", severity = 0.25 } } } } };
    private GameColonyMoveTestFixture(GameScenarioTestRuntime runtime, string client, string kind, string label)
    {
        Runtime = runtime; Client = client; Kind = kind; Label = label; Tile = client == "A" ? 7 : 9;
        silver = client == "A" ? 100 : 170; steel = client == "A" ? 37 : 51;
        var colony = new ColonyScenarioFixture(runtime.Run, runtime.Polling);
        Moves = new(runtime.Run, runtime.Polling, colony, new(runtime.Run, runtime.Polling, colony), World);
        Relocation = new(runtime.Run, Moves); Gravship = new(runtime.Run, runtime.Polling, runtime.Dialogs, Moves);
        // The same account can have historical colony generations. Only the current one is the move target.
        World.RegisterPlayerColonySites("rimtest-" + client, "historical", [new("rimtest-" + client, "historical", "old", "old-map", 2, "Old")]);
        PublishSite(); runtime.Script = Reply;
    }
    internal static async Task<GameColonyMoveTestFixture> Create(string client = "A", string kind = "caravan", string label = "Relocate")
        => new(await GameScenarioTestRuntime.Create(), client, kind, label);
    internal Task Run() => Kind == "flight" ? Gravship.Run(Client, Client == "B") : Relocation.Run(Client, Kind);
    internal bool Bad(string name) => Moved && (Fault == name || Reloaded && Fault == "reload-" + name);
    internal void CompleteMove()
    {
        Moved = true;
        if (!flying) steel = 10;
        PublishSite();
    }
    private void PublishSite()
    {
        World.RemovePlayerColonySites("rimtest-" + Client, Colony);
        if (Bad("site-generation")) return;
        int tile = Moved ? Tile : 0;
        if (Bad("site-tile")) tile++;
        var sites = new List<AIRsLight.ClashOfRim.Protocol.PlayerColonySiteDto> { new("rimtest-" + Client, Colony, "world", "home", tile, "Home") };
        if (Bad("site-duplicate")) sites.Add(new("rimtest-" + Client, Colony, "extra", "extra-map", tile + 1, "Extra"));
        World.RegisterPlayerColonySites("rimtest-" + Client, Colony, sites);
    }
    private object? Reply(string client, string method, JsonElement args)
    {
        Require(client == Client, "Move crossed clients.");
        switch (method)
        {
            case "clash.fixture.dlcPawn": Require(args.Bool("acknowledgeFixture"), "Missing DLC fixture consent."); return new { state = Dlc };
            case "clash.bank": return new { minLoanSilver = 20, minDurationDays = 3,
                activeLoan = loan ? (object)new { loanId = Bad("loan") ? "replacement" : Loan } : null };
            case "clash.invoke": return Invoke(args);
            case "time.step":
                if (args.Int("ticks") == 600) { Require(loan && !loanLanded, "Loan landing repeated or preceded borrowing."); silver += 20; loanLanded = true; }
                else Require(flying && args.Int("ticks") == 30, "Unexpected tick budget.");
                return null;
            case "clash.state": return State();
            case "clash.inventory": return Inventory();
            case "clash.dlc.pawns": return Bad("dlc") ? new { pawns = Array.Empty<object>() } : Dlc;
            case "clash.fixture.relocation":
                Require(args.String("kind") == Kind, "Wrong relocation source."); return PrepareFixture(args, false);
            case "clash.fixture.gravship":
                Require(args.Bool("site") == (Client == "B"), "Flight changed existing-site selection."); return PrepareFixture(args, true);
            case "clash.relocation":
                RelocationReads++;
                return new { label = Label, maps = new[] { new { tile = Tile, parent = Fault == "not-site" ? "Settlement" : "Site",
                    home = Fault == "already-home", settle = new { disabled = enemy ? Fault != "threat-ignored" : removedEnemy && Fault == "still-disabled" } } } };
            case "clash.fixture.relocationEnemy":
                Require(args.Bool("acknowledgeFixture") && args.String("map") == "destination", "Wrong threat fixture target.");
                if (args.TryGetProperty("remove", out var remove)) { Require(remove.GetString() == "enemy", "Wrong enemy removed."); enemy = false; removedEnemy = true; }
                else enemy = true;
                return new { id = "enemy" };
            case "world.select": Require(args.String("target") == "destination", "Wrong caravan selected."); return new { };
            case "world.gizmos": return new { items = new[] { new { label = "unrelated", type = "other", disabled = false },
                new { label = Label, type = "original-relocate", disabled = Fault == "disabled" } } };
            case "world.invokeGizmo":
                Require(args.String("target") == "destination" && args.String("type") == "original-relocate" && args.String("label") == Label,
                    "Relocation bypassed the localized original gizmo."); return new { };
            case "clash.relocation.settleMap": Require(args.String("map") == "destination", "Settled another map."); return new { };
            case "clash.relocation.confirm": Require(fixtureReady, "Move preceded fixture."); CompleteMove(); return new { };
            case "clash.gravship.pilot":
                Require(args.String("pawn") == "pilot" && args.String("console") == "console", "Wrong pilot or console."); flightPhase = 1; return new { };
            case "time.run":
                Require(flying && !Ticking && args.Int("tps") == 1200, "Overlapping flight tick ownership.");
                Ticking = true; Starts++; phaseReads = 0;
                if (Fault == "start-failed") throw new IOException("tick start response failed");
                return new { };
            case "time.cancel": Ticking = false; Stops++; return new { };
            case "clash.gravship.begin": Require(flightPhase == 1 && !Ticking, "Ceremony began before stopping manual ticks."); flightPhase = 2; dialog = true; return new { };
            case "time.set":
                Require(!Ticking, "Engine speed changed while manual ticking remained active.");
                if (args.String("speed") == "Normal") Require(flightPhase == 2, "Engine resumed at the wrong phase.");
                return new { };
            case "clash.gravship.destination":
                Require(args.Int("tile") == Tile && Runtime.Calls.Last(c => c.Method == "time.set").Args.String("speed") == "Normal",
                    "Destination selection did not release the engine clock.");
                flightPhase = 3; phaseReads = 0; marker = ExistingMarker; dialog = true; return new { };
            case "clash.gravship": return Flight();
            case "clash.gravship.placeLanding": Require(!marker, "Existing marker was replaced."); marker = true; return new { };
            case "clash.gravship.land": Require(marker && !dialog, "Landing ignored marker or warning."); CompleteMove(); return new { };
            case "dialogs.messages": return new { items = dialog ? new object[] { new { reference = new { handle = "flight-dialog" }, text = "abandon-map",
                buttons = new[] { new { id = "A", label = Label }, new { id = "B", label = "Cancel" } } } } : [] };
            case "clash.labels": return new { steamClientMissing = "Steam missing", ignore = "Ignore", quit = "Quit" };
            case "dialogs.choose": Require(args.String("dialog") == "flight-dialog" && args.String("button") == "A", "Wrong flight confirmation."); dialog = false; return new { };
            case "game.state":
                int read = Reloaded ? ++ReloadReads : 0;
                return new { epoch = Reloaded && (!Delayed || read > 1) ? "new-epoch" : "old-epoch",
                    playerHasControl = !Reloaded || !Delayed || read > 2, mapCount = !Reloaded || !Delayed || read > 3 ? 1 : 0 };
            case "game.save": Require(Moved, "Saved before destination commit."); saveName = args.String("name"); return new { };
            case "game.load": Require(args.String("name") == saveName, "Loaded another save."); Reloaded = true; PublishSite(); return new { };
            default: return null;
        }
    }
    private object Invoke(JsonElement args)
    {
        switch (args.String("action"))
        {
            case "StartRefreshBankStatus": break;
            case "StartCreateBankLoan":
                var input = args.GetProperty("arguments");
                Require(!loan && Runtime.Count("clash.fixture.dlcPawn") == 1 && input.Int("principalSilver") == 20 && input.Int("durationDays") == 3,
                    "Move did not borrow once using the current bank limits and DLC fixture."); loan = true; break;
            case "StartRepayBankLoan": Require(Reloaded, "Repayment skipped reload validation."); Repayments++; if (Fault != "repayment") loan = false; break;
            default: throw new InvalidOperationException("Unexpected move action: " + args.String("action"));
        }
        return new { };
    }
    private object PrepareFixture(JsonElement args, bool isFlight)
    {
        Require(loanLanded && args.Bool("acknowledgeFixture"), "Move fixture preceded loan landing or lacks consent.");
        fixtureReady = true; flying = isFlight;
        return new { tile = Tile, target = new { handle = "destination" }, pawns = Pawns,
            pawn = new { handle = "pilot" }, console = new { handle = "console" }, engine = "original-engine" };
    }
    private object State()
    {
        int read = Moved ? ++CommitReads : 0;
        if (Fault == "stale-snapshot" && read > 3) throw new IOException("bounded stale snapshot observation");
        string snapshot = !Moved || Fault == "stale-snapshot" || Delayed && read == 1 ? Snapshot
            : Fault == "empty-snapshot" ? "" : Fault == "blank-snapshot" ? " " : "after-" + Client;
        return new { colonyId = Bad("colony") ? "other-colony" : Colony, currentSnapshotId = snapshot,
            localAtomicMutationPending = Moved && Delayed && read == 2 };
    }
    private object Inventory()
    {
        string[] pawns = Bad("pawn-missing") ? [Pawns[0]] : Bad("pawn-duplicate") ? [Pawns[0], Pawns[0]] : Pawns.Reverse().ToArray();
        object map = new { tileId = Bad("tile") ? Tile + 1 : Tile,
            colonists = pawns.Select(id => new { id }).ToArray(),
            items = new Dictionary<string, int> { ["Silver"] = silver + (Bad("silver") ? 1 : 0), ["Steel"] = steel + (Bad("steel") ? -1 : 0) } };
        return new { maps = Bad("extra-home") ? new[] { map, map } : Bad("no-home") ? [] : new[] { map } };
    }
    private object Flight()
    {
        if (Ticking && (Fault == "poll-failed" || Fault == "second-poll-failed" && Starts == 2)) throw new IOException("flight observation failed");
        bool ready = !Delayed || ++phaseReads > 1;
        return new { ritualDialog = flightPhase == 1 && ready, choosingTile = flightPhase == 2 && ready,
            landing = flightPhase == 3 && ready, markers = marker ? new[] { "marker" } : [],
            engines = Fault == "engine-count" ? Array.Empty<object>() : new object[] {
                new { id = Fault == "engine-id" ? "replacement" : "original-engine", fuel = Fault == "fuel" ? 100f : 83.5f } } };
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public void Dispose() => Runtime.Dispose();
}
