using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientSessionFollowupTests
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
                foreach (ClientSessionFollowupSource source in Enum.GetValues<ClientSessionFollowupSource>())
                foreach (ClientSessionFollowup effect in Enum.GetValues<ClientSessionFollowup>())
                {
                    if (!Used(effect, source)) continue;
                    await Dispatch(effect, source, "none", "before"); cases++;
                    foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "game", "world", "snapshot", "password" })
                    { await Dispatch(effect, source, change, "before"); cases++; }
                    if (effect is ClientSessionFollowup.Events or ClientSessionFollowup.Markers
                        || effect == ClientSessionFollowup.ColonySites && source == ClientSessionFollowupSource.MapLogin)
                        foreach (string change in new[] { "login", "generation", "game", "world", "snapshot", "password" })
                        { await Dispatch(effect, source, change, "text"); cases++; }
                    if (effect == ClientSessionFollowup.ColonySites)
                        foreach (string change in new[] { "login", "generation", "game", "world", "snapshot", "password" })
                        { await Dispatch(effect, source, change, "appearance"); cases++; }
                }
                Queue(); cases++;
                foreach (string stage in new[] { "text", "appearance" }) { await CallbackFailure(stage); cases++; }
            }
            Console.WriteLine($"PASS: session followup dispatch ({cases} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private static bool Used(ClientSessionFollowup effect, ClientSessionFollowupSource source) => source switch
    {
        ClientSessionFollowupSource.InitialWorld => true,
        ClientSessionFollowupSource.ManualLogin => effect is ClientSessionFollowup.Events or ClientSessionFollowup.Players,
        _ => effect is not (ClientSessionFollowup.Markers or ClientSessionFollowup.Extensions)
    };
    private static async Task Dispatch(ClientSessionFollowup effect, ClientSessionFollowupSource source, string change, string stage)
    {
        var f = new Fixture(source); bool reached = stage == "before";
        if (stage == "before") f.Change(change);
        else f.Hook = point => { if (point == stage) { reached = true; f.Hook = null; f.Change(change); } };
        await f.Ports.PumpUntil(f.Module.Dispatch(effect, source, f.Step));
        bool allowed = change is "none" or "password" || change == "snapshot" && source == ClientSessionFollowupSource.ManualLogin;
        Require(reached && f.Calls.Count == (allowed ? 1 : 0), "Dispatch escaped source or skipped domain: " + effect + "/" + source + "/" + stage + "/" + change);
        if (!allowed) return;
        string expected = effect.ToString();
        Require(f.Calls.Single() == expected, "Wrong destination domain: " + effect);
        if (effect == ClientSessionFollowup.Chat) Require(f.ChatAfter == 0 && f.ChatLimit == 20, "Initial chat used incremental history.");
        if (effect == ClientSessionFollowup.ColonySites)
        {
            Require(f.SiteAppearance?.ColorHex == "#123456" && f.SiteCount == 1 && !f.Suppressed, "Colony followup lost appearance/sites/notification semantics.");
            Require(f.RegistrationRequested == (source == ClientSessionFollowupSource.MapLogin), "Map request and initial immediate registration were conflated.");
        }
        if (effect == ClientSessionFollowup.Extensions) Require(f.SiteCount == 0 && f.ExtensionCount == 1 && !f.RegistrationRequested, "Extension-only followup registered colony sites.");
        string? key = effect switch
        {
            ClientSessionFollowup.Events => source == ClientSessionFollowupSource.ManualLogin ? "ClashOfRim.Login.ReasonManual"
                : source == ClientSessionFollowupSource.MapLogin ? "ClashOfRim.MapSession.ReasonLogin" : "ClashOfRim.WorldBaseline.ReasonInitialSnapshotUploaded",
            ClientSessionFollowup.Markers => "ClashOfRim.WorldBaseline.ReasonSubmitted",
            ClientSessionFollowup.ColonySites when source == ClientSessionFollowupSource.MapLogin => "ClashOfRim.MapSession.ReasonLoginSucceeded",
            _ => null
        };
        Require(f.Keys.SequenceEqual(key is null ? Array.Empty<string>() : new[] { key }), "Wrong localized followup reason.");
        if (effect == ClientSessionFollowup.Events) Require(f.Reason == CultureInfo.CurrentCulture.Name + ":" + key, "Translated reason was not delivered to the event workflow.");
        Require(!f.Sync.IsBusy, "Followup leaked synchronization admission.");
    }
    private static void Queue()
    {
        var f = new Fixture(ClientSessionFollowupSource.MapLogin);
        var queue = new ModEventQueueSummaryDto { DirectlyProcessable = new() { new() { EventId = "event" } } };
        f.Module.AdoptQueue(queue); queue.DirectlyProcessable.Clear();
        Require(f.Inbox.CopyQueuedIds().SequenceEqual(new[] { "event" }), "Accepted queue did not reach the real inbox independently of its DTO.");
    }
    private static async Task CallbackFailure(string stage)
    {
        var f = new Fixture(ClientSessionFollowupSource.MapLogin);
        f.Hook = point => { if (point == stage) throw new InvalidOperationException("callback"); };
        bool threw = false;
        try { await f.Ports.PumpUntil(f.Module.Dispatch(ClientSessionFollowup.ColonySites, ClientSessionFollowupSource.MapLogin, f.Step)); }
        catch (InvalidOperationException ex) when (ex.Message == "callback") { threw = true; }
        Require(threw && f.Calls.IsEmpty && !f.Sync.IsBusy, "Failed preparation still started a followup.");
    }
    private sealed class Fixture : IClientSessionFollowupText, IClientEventReadProtocol, IClientEventReadPresentation,
        IClientPlayerDirectoryTransport, IClientPlayerDirectoryPresentation, IClientPlayerSelectionStore,
        IClientChatTransport, IClientChatPresentation, IClientWorldMarkerTransport, IClientWorldSynchronizationRuntime,
        IClientWorldSynchronizationSession, IClientWorldSynchronizationPresentation, IClientColonyRegistrationRuntime,
        IClientColonyRegistrationSession, IClientColonyRegistrationTransport, IClientColonyAppearanceStore, IClientSessionStreamTransport
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientEventInbox Inbox = new();
        internal readonly ClientWorldPublicationTestPorts World;
        internal readonly ClientSessionFollowups Module;
        internal readonly ConcurrentQueue<string> Calls = new();
        internal readonly List<string> Keys = new();
        internal Action<string>? Hook;
        internal long ChatAfter; internal int ChatLimit, SiteCount, ExtensionCount;
        internal bool Suppressed, RegistrationRequested;
        internal ModColonyAppearanceDto? SiteAppearance;
        internal string? Reason;
        private readonly Func<bool> current;
        internal Fixture(ClientSessionFollowupSource source)
        {
            ClientSessionWork.Reset(); var connection = new ClientSessionConnection(this);
            Session = new(Ports, Ports, connection); Session.PublishLogin(new() { AuthToken = "auth", SessionId = "session" });
            World = new(Session, Ports);
            var owner = Session.CaptureOwnership(); var context = Ports.ReadContext(); object game = Ports.GameOwner; string world = World.WorldId;
            current = () => Session.Owns(owner, context, source != ClientSessionFollowupSource.ManualLogin) && ReferenceEquals(game, Ports.GameOwner) && world == World.WorldId;
            var mutations = new ClientLocalMutationCoordinator();
            var reader = new ClientEventReader(Session, Inbox, this, Ports); var state = new ClientEventState(reader, this);
            var events = new ClientEventRefresh(Session, reader, Inbox, Sync, mutations, null!, null!, this, state);
            var players = new ClientPlayerDirectory(Session, Ports, Sync, mutations, this, this, this, null!);
            var registration = new ClientColonyRegistration(Session, World, mutations, this, this, World.Flow);
            registration.Diagnostic += (message, _) => { if (message.Contains("refresh requested", StringComparison.Ordinal)) RegistrationRequested = true; };
            Module = new(events, players, new ClientChat(Session, this, this), registration, new ClientColonyAppearance(Session, this, World),
                new ClientWorldSynchronization(Session, World, mutations, World.Markers, this, this, this, null!),
                new ClientSessionNotifications(Session, connection, state, null!, null!), this);
        }
        internal bool Step(Action action)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (!current()) return false; action(); return current();
        }
        internal void Change(string kind)
        {
            switch (kind)
            {
                case "user": Ports.User = "bob"; break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "login": Session.PublishLogin(new() { AuthToken = "auth", SessionId = "session" }); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "game": Ports.GameOwner = new(); break;
                case "world": World.LocalWorldId = "other"; break;
                case "snapshot": Ports.Snapshot = "next"; break;
                case "password": Ports.Password = "changed"; break;
            }
        }
        private Task<ClashOfRimClientNetworkResult<T>> Sent<T>(string name, ClashOfRimClientNetworkContext context)
        {
            Require(context.UserId == "alice" && context.AuthToken == "auth" && context.ColonyId == "colony", "Followup borrowed a different identity.");
            Calls.Enqueue(name); return Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("controlled", "stop after dispatch"));
        }
        public string Text(string key) { Keys.Add(key); Hook?.Invoke("text"); return CultureInfo.CurrentCulture.Name + ":" + key; }
        public string Text(string key, params (string Name, object? Value)[] args) => key;
        public string Format(ClientEventRefreshNotice notice) { if (notice.Stage == ClientEventRefreshStage.Started) Reason = notice.Reason; return notice.Stage.ToString(); }
        public void Diagnose(ClientEventRefreshNotice notice) { }
        public void Diagnose(Exception error) { }
        public void Diagnose(string error) { }
        public string MergedReason => "merged";
        public string ManualDetailsReason => "details";
        public string Admission(LocalMutationAdmission admission, LocalMutationState? state) => admission.ToString();
        public string Summary(IReadOnlyCollection<ModPlayerSummaryDto> players) => "players";
        public void Reject(string text) { }
        public void Status(string text) { }
        public void Enqueue(Action action, Action discard) => Ports.Enqueue(action);
        public Task<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>> PullAsync(ClashOfRimClientNetworkContext context, CancellationToken token) => Sent<ModPullPendingEventsResponseDto>("Events", context);
        public Task<ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto>> WaitAsync(ClashOfRimClientNetworkContext context, long version, int seconds, CancellationToken token) => throw new NotSupportedException();
        public Task<ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto>> DetailsAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<string> ids, CancellationToken token) => throw new NotSupportedException();
        public Task<ClashOfRimClientNetworkResult<ModListPlayersResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, CancellationToken token) => Sent<ModListPlayersResponseDto>("Players", context);
        public Task<ClashOfRimClientNetworkResult<ModListChatMessagesResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, long after, int limit, CancellationToken token)
        { ChatAfter = after; ChatLimit = limit; return Sent<ModListChatMessagesResponseDto>("Chat", context); }
        public Task<ClashOfRimClientNetworkResult<ModSendChatMessageResponseDto>> SendAsync(ClashOfRimClientNetworkContext context, string? target, string text, CancellationToken token) => throw new NotSupportedException();
        public Task<ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token) => Sent<ModWorldMapMarkerDeliveryDto>("Markers", context);
        public Task<ClashOfRimClientNetworkResult<ModSyncRuntimeWorldObjectsResponseDto>> WriteAsync(ClashOfRimClientNetworkContext context, IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> objects, CancellationToken token) => throw new NotSupportedException();
        public Task<ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>> RegisterAsync(ClashOfRimClientNetworkContext context,
            IReadOnlyCollection<ModPlayerColonySiteDto> sites, IReadOnlyCollection<ModWorldConfigurationExtensionDto>? extensions, bool suppress, CancellationToken token)
        { SiteCount = sites.Count; ExtensionCount = extensions?.Count ?? 0; Suppressed = suppress; return Sent<ModRegisterPlayerColonySitesResponseDto>(sites.Count == 0 ? "Extensions" : "ColonySites", context); }
        public Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(ClashOfRimClientNetworkContext context, string id, long notificationVersion, long worldVersion,
            Action<ModSessionStreamEvent> receive, CancellationToken token)
        { Require(id == "session", "Presence lost accepted SessionId."); return Sent<ModSessionStreamClosedDto>("Presence", context); }
        public ClientColonyAppearanceAccount ReadAccount() => World.ReadAccount();
        public string? Read(string key) { Hook?.Invoke("appearance"); return ClientColonyAppearanceCodec.Encode(new("Manual", "icon", "", "#123456")); }
        public void Write(string key, string? value) => throw new NotSupportedException();
        ClientPlayerSelection IClientPlayerSelectionStore.Read() => new("", "", "");
        public void Write(ClientPlayerSelection selection) => throw new NotSupportedException();
        public void Persist() => throw new NotSupportedException();
        IClientColonyRegistrationSession IClientColonyRegistrationRuntime.Bind() => this;
        IClientWorldSynchronizationSession IClientWorldSynchronizationRuntime.Bind() => this;
        public bool IsCurrent => true;
        public bool Playing => true;
        public bool HasWorld => true;
        public bool HasWorldObjects => true;
        public bool HasColonyContext => true;
        public bool HasExtensionCollectors => true;
        public bool HasRemoteSession => false;
        public int Tick => 0;
        public IReadOnlyCollection<ModPlayerColonySiteDto> CaptureSites(string user, string colony, ModColonyAppearanceDto? appearance)
        { SiteAppearance = appearance; return new[] { new ModPlayerColonySiteDto { UserId = user, ColonyId = colony, Tile = 4 } }; }
        public IReadOnlyCollection<ModWorldConfigurationExtensionDto> CaptureExtensions(string user, string colony, string snapshot)
            => new[] { new ModWorldConfigurationExtensionDto { ProviderId = "test", Kind = "test", PayloadJson = "{}" } };
        public IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureObjects() => throw new NotSupportedException();
        public IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureCaravans() => throw new NotSupportedException();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
