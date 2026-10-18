using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Diagnostics;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.World;
using HarmonyLib;

internal static class ClientSessionCleanupTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture; int cases = 0;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                cases += HostStatusReads();
                foreach (string mode in new[] { "end", "owned", "clear", "expiry" })
                { await Scope(mode); cases++; }
                foreach (string stage in new[] { "previews", "bank", "mercenaries", "shop", "trade", "icons", "world", "snapshot", "selection", "players", "InitialQueue", "InitialDetails", "InitialAction" })
                foreach (string change in new[] { "login", "same-login", "generation" })
                { await Reentry(stage, change); cases++; }
                foreach (string stage in new[] { "bank", "mercenaries", "shop", "trade", "players", "InitialQueue", "InitialDetails", "InitialAction" })
                { StatusSemantics(stage); cases++; }
                await RepeatedExit(); cases++;
            }
            Console.WriteLine($"PASS: session cleanup ({cases} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }

    private static int HostStatusReads()
    {
        var harmony = new Harmony("ClashOfRim.Tests.HostStatusReads");
        harmony.Patch(AccessTools.Method(typeof(ClashOfRimText), "Key", new[] { typeof(string) }),
            prefix: new HarmonyMethod(typeof(ClientSessionCleanupTests), nameof(HostText)));
        string[] properties = { nameof(ClashOfRimMod.LoginStatus), nameof(ClashOfRimMod.EventQueueStatus), nameof(ClashOfRimMod.EventDetailsStatus),
            nameof(ClashOfRimMod.GiftProcessingStatus), nameof(ClashOfRimMod.SnapshotUploadStatus), nameof(ClashOfRimMod.PresenceStatus),
            nameof(ClashOfRimMod.TradeStatus), nameof(ClashOfRimMod.WorldMapStatus), nameof(ClashOfRimMod.BankStatus),
            nameof(ClashOfRimMod.MercenaryStatus), nameof(ClashOfRimMod.ServerShopStatus) };
        try
        {
            foreach (string firstRead in properties)
            {
                var f = new Fixture(); var host = Host(f);
                f.Diagnostics.LoginStatus = "login-progress"; f.Diagnostics.SnapshotUploadStatus = "snapshot-progress";
                f.Diagnostics.PresenceStatus = "presence-progress"; f.Diagnostics.WorldMapStatus = "world-progress";
                f.Bank.Message = "bank-progress"; f.Shop.Message = "shop-progress"; f.Trade.Message = "trade-progress";
                f.Mercenaries.Message = "mercenary-progress"; f.Events.ActionStatus = "gift-progress";
                f.Events.SetQueueStatus("queue-progress"); f.Events.SetDetailsStatus("details-progress");
                _ = typeof(ClashOfRimMod).GetProperty(firstRead, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
                Require(host.LoginStatus == "login-progress" && host.SnapshotUploadStatus == "snapshot-progress"
                    && host.PresenceStatus == "presence-progress" && host.WorldMapStatus == "world-progress"
                    && host.BankStatus == "bank-progress" && host.ServerShopStatus == "shop-progress" && host.TradeStatus == "trade-progress"
                    && host.MercenaryStatus == "mercenary-progress" && host.GiftProcessingStatus == "gift-progress"
                    && host.EventQueueStatus == "queue-progress" && host.EventDetailsStatus == "details-progress",
                    "First UI status read overwrote published domain state: " + firstRead);
            }
            {
                var f = new Fixture(); var host = Host(f);
                foreach (var (property, key) in new[] { (nameof(ClashOfRimMod.LoginStatus), "NotLoggedIn"),
                    (nameof(ClashOfRimMod.SnapshotUploadStatus), "SnapshotUploadNotStarted"),
                    (nameof(ClashOfRimMod.PresenceStatus), "PresenceNotStarted"), (nameof(ClashOfRimMod.WorldMapStatus), "WorldMapNotSynced") })
                    Require((string)typeof(ClashOfRimMod).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!
                        == CultureInfo.CurrentCulture.Name + ":ClashOfRim.Status." + key, "Missing status did not use its own localized default.");
                f.Bank.Message = "completed"; f.Shop.Message = "completed"; f.Trade.Message = "completed";
                f.Bank.Reset(); f.Shop.Reset(); f.Trade.Reset();
                Require(host.BankStatus == f.Bank.Message && host.ServerShopStatus == f.Shop.Message && host.TradeStatus == f.Trade.Message,
                    "Host reintroduced defaults owned by domain reset.");
            }
            {
                var f = new Fixture(); var host = Host(f);
                f.Diagnostics.LoginStatus = f.Diagnostics.SnapshotUploadStatus = f.Diagnostics.PresenceStatus = f.Diagnostics.WorldMapStatus = "";
                Require(host.LoginStatus == "" && host.SnapshotUploadStatus == "" && host.PresenceStatus == "" && host.WorldMapStatus == "",
                    "Explicitly cleared presentation text was replaced by a default.");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        return properties.Length + 2;
    }
    // An isolated legacy UI shell reads real domain state; its constructor's game
    // registration and Harmony setup are unrelated to these read-only properties.
    private static ClashOfRimMod Host(Fixture f)
    {
        var host = (ClashOfRimMod)RuntimeHelpers.GetUninitializedObject(typeof(ClashOfRimMod));
        Set(host, "bankState", f.Bank); Set(host, "shopState", f.Shop); Set(host, "tradeState", f.Trade);
        Set(host, "mercenaryState", f.Mercenaries); Set(host, "eventState", f.Events);
        Set(host, "diagnostics", f.Diagnostics);
        return host;
    }
    private static void Set(ClashOfRimMod host, string field, object value)
        => typeof(ClashOfRimMod).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);
    private static bool HostText(string __0, ref string __result) { __result = CultureInfo.CurrentCulture.Name + ":" + __0; return false; }

    private static async Task Scope(string mode)
    {
        var f = new Fixture(); f.Seed("old");
        bool full = mode is "end" or "owned";
        f.BeforeAuthenticationClear = () =>
        {
            if (full) f.AssertCleared();
            else Require(f.Previews && f.Icons && f.World.WorldId == "old" && f.Ports.Snapshot == "old"
                && f.Lineage == "old" && f.Selection.UserId == "old" && !f.SourceGame.BlocksCurrent
                && f.Inbox.HasPendingConfirmation("old") && f.Mutations.IsPending,
                "In-place identity cleanup ran explicit-exit domain teardown: " + mode);
        };
        if (mode == "end") await f.Session.End(false);
        else if (mode == "owned") Require(f.Session.EndOwned(f.Session.CaptureOwnership(), f.Ports.ReadContext()) is not null, "Owned cleanup lost its exit.");
        else if (mode == "clear") f.Session.ClearIdentity();
        else Require(f.Session.CompleteUnavailable(f.Session.BeginUnavailable("auth", "session")!), "Expiry did not complete.");
        Require(f.ClearedWrites == 1 && f.Ports.Auth == "", "Exit did not persist once after cleanup.");
    }

    private static async Task Reentry(string stage, string change)
    {
        var f = new Fixture(); f.Seed("old"); bool ran = false;
        f.Hook = point =>
        {
            if (point != stage) return;
            f.Hook = null; ran = true;
            if (change == "generation") ClientSessionWork.Reset();
            else f.Session.PublishLogin(new() { SessionId = change == "same-login" ? "session" : "new-session", AuthToken = "auth", Result = new() { Accepted = true } });
            f.Seed("new");
        };
        if (f.Status(stage) is { } status)
        {
            status.Reset();
            _ = status.Read();
        }
        else await f.Session.End(false);
        Require(ran, "Cleanup observation was not reached: " + stage);
        Require(f.ClearedWrites == 0 && f.Ports.Auth == "auth" && f.World.WorldId == "new" && f.Ports.Snapshot == "new"
            && f.Lineage == "new" && f.Selection.UserId == "new" && f.Previews && f.Icons,
            "Old cleanup reached successor settings/resources: " + stage + "/" + change);
        Require(f.Bank.Message == "new" && f.Shop.Message == "new" && f.Trade.Message == "new"
            && f.Mercenaries.Message == "new" && f.Players.Message == "new" && f.Setup.LanguageAccepted
            && f.Events.QueueStatus == "new" && f.Events.DetailsStatus == "new" && f.Events.ActionStatus == "new",
            "Old cleanup overwrote successor domain state: " + stage + "/" + change);
        Require(f.Sync.IsBusy && f.Mutations.IsPending && f.Inbox.HasPendingConfirmation("new"), "Old exit released successor work.");
    }

    private static void StatusSemantics(string stage)
    {
        var f = new Fixture(); f.Seed("old"); var status = f.Status(stage)!.Value;
        f.Hook = point => throw new InvalidOperationException("Reset invoked presentation: " + point);
        status.Reset();
        f.Hook = null;
        Require(status.Read() == CultureInfo.CurrentCulture.Name + ":" + stage, "Reset lost the existing localized default: " + stage);
        status.Write(""); Require(status.Read() == "", "Explicitly empty status became a default: " + stage);
    }

    private static async Task RepeatedExit()
    {
        var f = new Fixture(); f.Seed("old");
        var exit = f.Session.EndOwned(f.Session.CaptureOwnership(), f.Ports.ReadContext())!;
        Require(exit is not null && f.ClearedWrites == 1, "Initial exit did not clear state.");
        Require(f.Session.ContinueExit(exit!, () => _ = f.Session.End(true)) && f.ClearedWrites == 1,
            "Scene disconnect repeated an owned cleanup.");
        f.Session.PublishLogin(new() { SessionId = "next", AuthToken = "auth", Result = new() { Accepted = true } });
        f.Seed("next"); await f.Session.End(false);
        Require(f.ClearedWrites == 2, "Cleanup registration did not survive the next login.");
    }

    // Use real domains and shared engine test ports. Unused request transports are
    // intentionally absent: teardown must never issue new business requests.
    private sealed class Fixture : IClientSessionIdentityStore, IClientSessionExitStore, IClientSessionExitResources,
        IClientWorldSourceGame, IClientPlayerSelectionStore, IClientBankPresentation, IClientShopPresentation,
        IClientTradeMarketPresentation, IClientMercenaryHiringPresentation, IClientPlayerDirectoryPresentation,
        IClientEventReadPresentation, IClientColonyRegistrationRuntime, IClientColonyRegistrationSession,
        IClientWorldSynchronizationRuntime, IClientWorldSynchronizationSession
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientEventInbox Inbox = new();
        internal readonly ClientEventState Events;
        internal readonly ClientRuntimeDiagnostics Diagnostics;
        internal readonly ClientBankState Bank;
        internal readonly ClientShopState Shop;
        internal readonly ClientTradeState Trade;
        internal readonly ClientMercenaryState Mercenaries;
        internal readonly ClientPlayerDirectory Players;
        internal readonly ClientWorldSetupState Setup;
        internal readonly ClientColonyRegistration Registration;
        internal readonly ClientWorldSourceGame SourceGame;
        internal readonly ClientWorldPublicationTestPorts World;
        internal readonly ClientChat Chat;
        internal readonly ClientAdministration Administration;
        internal readonly ClientSessionNotifications Notifications;
        internal string Lineage = "";
        internal ClientPlayerSelection Selection = new("", "", "");
        internal bool Previews, Icons;
        internal int ClearedWrites;
        internal Action<string>? Hook;
        internal Action? BeforeAuthenticationClear;
        internal Fixture()
        {
            ClientSessionWork.Reset();
            var connection = new ClientSessionConnection(Ports);
            Session = new(this, Ports, connection);
            Session.PublishLogin(new() { SessionId = "session", AuthToken = "auth", Result = new() { Accepted = true } });
            World = new(Session, Ports);
            SourceGame = new(this); Setup = new(Session);
            Events = new(new ClientEventReader(Session, Inbox, null!, Ports), this);
            Diagnostics = new(Session, Events);
            Bank = new(Session, Ports, this); Shop = new(Session, Ports, this); Trade = new(Session, Ports, this);
            Mercenaries = new(Session, Ports, this);
            var recovery = new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, new EventModuleFailureView(Ports));
            var incidents = new ClientMercenaryIncidents(Session, Mercenaries, Bank, Sync, Mutations, null!, null!, recovery, null!, null!, null!, null!);
            Registration = new(Session, World, Mutations, this, null!, World.Flow);
            var worldSync = new ClientWorldSynchronization(Session, World, Mutations, World.Markers, this, null!, null!, null!);
            Players = new(Session, Ports, Sync, Mutations, null!, this, this, null!);
            Chat = new(Session, null!, null!); Administration = new(new ClientAdminAccess(Session), null!, null!);
            Notifications = new(Session, connection, Events, null!, null!);
            ClientSessionCleanup.Attach(Session, Sync, Mutations, Inbox, Events, SourceGame, this, Registration, Bank,
                Mercenaries, incidents, Shop, Trade, Chat, World.Markers, worldSync, this, Administration,
                Notifications, Setup, Players, World.Sites);
        }
        internal void Seed(string value)
        {
            World.LocalWorldId = Ports.Snapshot = Lineage = value;
            Selection = new(value, value, value); Previews = Icons = true;
            Bank.View = new(); Bank.Message = Shop.Message = Trade.Message = Mercenaries.Message = Players.Message = value;
            Setup.LanguageAccepted = true; Registration.Suppressed = true;
            Events.SetQueueStatus(value); Events.SetDetailsStatus(value); Events.ActionStatus = value;
            Inbox.TryTrackPendingConfirmation(value);
            Sync.Reset(); Require(Sync.TryBeginManual(out _), "Could not seed pending work.");
            Mutations.Begin(LocalMutationKind.SnapshotRestore, value);
        }
        internal (Action Reset, Func<string> Read, Action<string> Write)? Status(string stage) => stage switch
        {
            "bank" => (Bank.Reset, () => Bank.Message, text => Bank.Message = text),
            "mercenaries" => (Mercenaries.Reset, () => Mercenaries.Message, text => Mercenaries.Message = text),
            "shop" => (Shop.Reset, () => Shop.Message, text => Shop.Message = text),
            "trade" => (Trade.Reset, () => Trade.Message, text => Trade.Message = text),
            "players" => (Players.Reset, () => Players.Message, text => Players.Message = text),
            "InitialQueue" => (Events.Reset, () => Events.QueueStatus, Events.SetQueueStatus),
            "InitialDetails" => (Events.Reset, () => Events.DetailsStatus, Events.SetDetailsStatus),
            "InitialAction" => (Events.Reset, () => Events.ActionStatus, text => Events.ActionStatus = text),
            _ => null
        };
        internal void AssertCleared()
        {
            Require(!Sync.IsBusy && !Mutations.IsPending && Inbox.PendingConfirmationCount == 0 && SourceGame.BlocksCurrent,
                "Explicit exit retained admitted work or failed to block the exiting scene.");
            Require(!Previews && !Icons && World.WorldId == "" && Ports.Snapshot == "" && Lineage == "" && Selection.UserId == "",
                "Exit credentials were persisted before transient settings/resources were cleared.");
            Require(Bank.View is null && Shop.CopyListings().Count == 0 && Trade.Count == 0 && !Mercenaries.Busy
                && !Players.Busy && !Setup.Pending && !Setup.LanguageAccepted && !Registration.Suppressed
                && Chat.CopyMessages().Count == 0 && Notifications.WorldVersion == 0 && Events.NotificationVersion == 0,
                "Exit retained domain state.");
        }
        private string Step(string point) { Hook?.Invoke(point); return CultureInfo.CurrentCulture.Name + ":" + point; }
        public ClashOfRimClientNetworkContext ReadContext() => Ports.ReadContext();
        public void Write(string? userId, string? displayName, string authToken)
        {
            if (authToken.Length == 0) { BeforeAuthenticationClear?.Invoke(); ClearedWrites++; }
            Ports.Write(userId, displayName, authToken);
        }
        public void ClearWorld() { World.LocalWorldId = ""; Step("world"); }
        public void ClearSnapshot() { Ports.Snapshot = Lineage = ""; Step("snapshot"); }
        public void ClearPawnPreviews() { Previews = false; Step("previews"); }
        public void ClearWorldIcons() { Icons = false; Step("icons"); }
        public object Current => Ports.GameOwner;
        public ClientPlayerSelection Read() => Selection;
        public void Write(ClientPlayerSelection value) { Selection = value; Step("selection"); }
        public void Persist() => throw new InvalidOperationException("Selection must persist with authentication.");
        string IClientBankPresentation.Text(string key, params (string Name, object? Value)[] args) => Step("bank");
        string IClientShopPresentation.Text(string key, params (string Name, object? Value)[] args) => Step("shop");
        string IClientPlayerDirectoryPresentation.Text(string key, params (string Name, object? Value)[] args) => Step("players");
        public string Text(string key, params (string Name, object? Value)[] args) => key;
        public string InitialStatus => Step("trade");
        public string Idle => Step("mercenaries");
        public string Busy => throw new NotSupportedException();
        public string BaseSnapshotChanged => throw new NotSupportedException();
        public string NotAdministrator => throw new NotSupportedException();
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => throw new NotSupportedException();
        public string Summary(ModBankStatusResponseDto? value) => throw new NotSupportedException();
        public string Summary(IReadOnlyCollection<ModPlayerSummaryDto> value) => throw new NotSupportedException();
        public void PreparePreviews(IReadOnlyList<ModServerShopListingDto> values) => throw new NotSupportedException();
        public void PreparePreviews(IReadOnlyList<ModTradeOrderSummaryDto> values) => throw new NotSupportedException();
        public void Message(string text, bool rejected) => throw new NotSupportedException();
        public void Diagnose(Exception error) => throw new NotSupportedException();
        public string Format(ClientEventRefreshNotice notice) => Step(notice.Stage.ToString());
        public void Diagnose(ClientEventRefreshNotice notice) => throw new NotSupportedException();
        public string MergedReason => throw new NotSupportedException();
        public string ManualDetailsReason => throw new NotSupportedException();
        IClientColonyRegistrationSession IClientColonyRegistrationRuntime.Bind() => this;
        IClientWorldSynchronizationSession IClientWorldSynchronizationRuntime.Bind() => this;
        public bool IsCurrent => true;
        public bool Playing => true;
        public bool HasWorld => true;
        public bool HasWorldObjects => true;
        public bool HasColonyContext => true;
        public bool HasExtensionCollectors => false;
        public bool HasRemoteSession => false;
        public int Tick => 0;
        public IReadOnlyCollection<ModPlayerColonySiteDto> CaptureSites(string user, string colony, ModColonyAppearanceDto? appearance) => throw new NotSupportedException();
        public IReadOnlyCollection<ModWorldConfigurationExtensionDto> CaptureExtensions(string user, string colony, string snapshot) => throw new NotSupportedException();
        public IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureObjects() => throw new NotSupportedException();
        public IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureCaravans() => throw new NotSupportedException();
        public void Enqueue(Action action, Action discard) => throw new NotSupportedException();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
