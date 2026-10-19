using System;
using System.Globalization;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientWorldObjectCommandsTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                Ownership(); Reentry();
                foreach (string mode in new[] { ClientRemoteMapModes.Scout, ClientRemoteMapModes.FriendlyObservation, ClientRemoteMapModes.RaidObservation })
                    await Observation(mode);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static void Ownership()
    {
        foreach (string change in new[] { "server", "user", "colony", "auth", "password", "steam", "game", "generation", "login", "unavailable", "reset",
            "removed", "marker", "kind", "owner", "target-colony", "map", "world-object", "event", "tile", "layer", "raid", "trade", "support", "relation", "allied" })
        foreach (bool duringValidation in new[] { false, true })
        {
            var f = new Fixture(); var selection = f.Capture(); int effects = 0;
            if (!duringValidation) f.Change(change);
            bool committed = selection.TryCommit(() => { if (duringValidation) f.Change(change); return true; }, () => effects++);
            Check(!committed && effects == 0 && !selection.IsCurrent, "Old world-object choice crossed " + change + " duringValidation=" + duringValidation);
            selection.TryPresent(() => effects++); selection.TryStep(() => effects++);
            ClientObservationMapsTests.Game(() => Check(selection.Observe() is null, "Old choice started observation after " + change));
            Check(effects == 0 && f.Core.Notices == 0, "Expired choice published side effects.");
        }
        var live = new Fixture(); var frozen = live.Capture();
        live.Core.Ports.Snapshot = "new-local-snapshot"; live.Core.Target.SnapshotId = "new-remote-snapshot";
        live.Core.Target.Label = "updated-label"; live.Core.Target.PathTiles![0] = 99;
        var copy = frozen.CopyTarget(); copy.OwnerUserId = "caller-mutation"; copy.PathTiles![0] = 88;
        Check(frozen.IsCurrent && frozen.CopyTarget().OwnerUserId == "bob" && frozen.CopyTarget().Label == "original"
            && frozen.CopyTarget().PathTiles![0] == 7, "Harmless updates invalidated a choice or escaped target freezing.");
        var captureReentry = new Fixture(); captureReentry.ReadHook = () => captureReentry.Change("game");
        Check(captureReentry.Commands.Capture(captureReentry) is null, "Capture accepted an engine read from another Game.");
        var checkReentry = new Fixture(); var original = checkReentry.Capture();
        checkReentry.ReadHook = () => checkReentry.Change("auth");
        Check(!original.TryCommit(() => true, () => throw new Exception("crossed reentry")), "Target reader changed session after ownership check.");
        foreach (string credential in new[] { "password", "steam" })
        {
            var credentials = new Fixture(); var stale = credentials.Capture(); credentials.Change(credential);
            Check(!stale.IsCurrent && credentials.Capture() is { IsCurrent: true }, "Credential change prevented fresh menus: " + credential);
        }
    }

    private static void Reentry()
    {
        var f = new Fixture(); var selection = f.Capture(); int validations = 0, effects = 0, shown = 0;
        selection.TryPresent(() => { shown++; selection.TryPresent(() => shown++); });
        selection.TryPresent(() => shown++); Check(shown == 1, "Repeated selection opened duplicate confirmations.");
        Check(!selection.TryCommit(() => false, () => effects++), "Rejected validation committed.");
        Check(selection.TryCommit(() =>
        {
            validations++;
            Check(!selection.TryCommit(() => true, () => effects++), "Validation reentry committed another launch.");
            return true;
        }, () => { effects++; selection.TryCommit(() => true, () => effects++); }), "Valid launch was blocked after a rejected validation.");
        selection.TryCommit(() => { validations++; return true; }, () => effects++);
        Check(validations == 1 && effects == 1, "Duplicate confirmation repeated validation or launch.");
        var throwing = f.Capture();
        try { throwing.TryCommit(() => throw new InvalidOperationException("validation"), () => effects++); }
        catch (InvalidOperationException) { }
        Check(throwing.TryCommit(() => true, () => effects++), "Throwing validation permanently locked the choice.");
        var launchThrows = f.Capture();
        try { launchThrows.TryCommit(() => true, () => throw new InvalidOperationException("launch")); }
        catch (InvalidOperationException) { }
        Check(!launchThrows.TryCommit(() => true, () => effects++), "Throwing launch could be invoked again with another idempotency key.");
        var synchronous = f.Capture(); int launched = 0;
        synchronous.TryPresent(() => synchronous.TryCommit(() => true, () => launched++));
        Check(launched == 1, "Synchronous confirmation could not launch.");
    }

    private static async Task Observation(string mode)
    {
        var f = new Fixture(); f.Allied = mode == ClientRemoteMapModes.FriendlyObservation;
        f.Core.Target.Kind = mode == ClientRemoteMapModes.RaidObservation ? "ActiveRaidTarget" : "TradeableColony";
        var choice = f.Capture(); Check(choice.ObservationMode == mode, "Wrong observation authorization mode.");
        f.Blocked = true;
        ClientObservationMapsTests.Game(() => Check(choice.Observe() is null, "Active map did not block observation."));
        Check(f.Rejected == 1 && f.Core.DownloadCalls == 0, "Active-map refusal started network work.");
        f.Blocked = false;
        Check(f.Core.Sync.TryBeginManual(out var manual), "Fixture manual admission failed.");
        using (manual) ClientObservationMapsTests.Game(() => Check(choice.Observe() is null, "Busy observation acquired another admission."));
        Task? work = null;
        ClientObservationMapsTests.Game(() =>
        {
            work = choice.Observe(); Check(work is not null, "Observation could not retry after busy refusal.");
            Check(choice.Observe() is null, "Repeated click started another observation.");
        });
        f.Core.Target.Label = "changed-after-click";
        (await f.Core.Next())(); (await f.Core.Next())();
        await work!.WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Core.DownloadCalls == 1 && f.Core.LoadedCount == 1 && f.Core.Opened!.Target.Label == "original"
            && f.Core.DownloadTarget!.AuthorizationScope == mode, "Observation lost its frozen target or existing workflow.");
        var invalid = new Fixture(); invalid.Core.Target.Kind = "ActiveRaidTarget"; invalid.Core.Target.RelatedEventId = "";
        Check(invalid.Capture().ObservationMode is null, "Raid observation without event was offered.");
        var reenter = new Fixture(); var original = reenter.Capture(); reenter.BlockHook = () => reenter.Change("user");
        ClientObservationMapsTests.Game(() => Check(original.Observe() is null, "Active-map check crossed another account."));
        Check(reenter.Core.Notices == 0 && reenter.Rejected == 0, "Reentered observation published to another account.");
    }

    internal sealed class Fixture : IClientWorldObjectCommandTarget, IClientWorldObjectCommandRuntime
    {
        internal readonly ClientObservationMapsTests.Fixture Core = new(login: false);
        internal readonly ClientWorldObjectCommands Commands;
        internal bool Removed, Allied, Blocked;
        internal int Rejected;
        internal Action? ReadHook, BlockHook;
        internal Fixture()
        {
            Core.Target.Kind = "TradeableColony"; Core.Target.MarkerId = "marker"; Core.Target.Label = "original";
            Core.Target.CanRaid = true; Core.Target.RelationKind = "Hostile";
            Commands = new(Core.Session, Core.Ports, Core.Maps, this);
        }
        internal ClientWorldObjectCommands.Selection Capture() => Commands.Capture(this)!;
        public ClientWorldObjectTargetState? Read() { ReadHook?.Invoke(); return Removed ? null : new(Core.Target, Allied); }
        public bool HasActiveRemoteMap { get { BlockHook?.Invoke(); return Blocked; } }
        public void Reject(string key) { Check(key == "ClashOfRim.RemoteSessionMap.ActiveSessionBlocksOpen", "Wrong active map diagnostic."); Rejected++; }
        internal void Change(string kind)
        {
            var p = Core.Ports; var t = Core.Target;
            switch (kind)
            {
                case "server": p.Server = "http://other"; break;
                case "user": p.User = "other"; break;
                case "colony": p.Colony = "other"; break;
                case "auth": p.Auth = "other"; break;
                case "password": p.Password = "other"; break;
                case "steam": p.SteamTicket = "other"; break;
                case "game": p.GameOwner = new(); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "login": Core.Session.PublishLogin(new() { AuthenticatedUserId = p.User, AuthToken = p.Auth, SessionId = "new" }); break;
                case "unavailable": Core.Session.BeginUnavailable(p.Auth, null); break;
                case "reset": Commands.Reset(); break;
                case "removed": Removed = true; break;
                case "marker": t.MarkerId = "other"; break;
                case "kind": t.Kind = "other"; break;
                case "owner": t.OwnerUserId = "other"; break;
                case "target-colony": t.OwnerColonyId = "other"; break;
                case "map": t.MapId = "other"; break;
                case "world-object": t.WorldObjectId = "other"; break;
                case "event": t.RelatedEventId = "other"; break;
                case "tile": t.Tile++; break;
                case "layer": t.TileLayerId++; break;
                case "raid": t.CanRaid = !t.CanRaid; break;
                case "trade": t.CanTrade = !t.CanTrade; break;
                case "support": t.CanReinforce = !t.CanReinforce; break;
                case "relation": t.RelationKind = "Ally"; break;
                case "allied": Allied = !Allied; break;
                default: throw new Exception(kind);
            }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
