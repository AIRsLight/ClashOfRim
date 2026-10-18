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

using AIRsLight.ClashOfRim.Modules.Shop;

// Shared real domain graph with controlled transport/engine ports for followup tests.
internal sealed class ClientFollowupTestPorts : IClientSessionFollowupText, IClientEventReadProtocol, IClientEventReadPresentation,
    IClientPlayerDirectoryTransport, IClientPlayerDirectoryPresentation, IClientPlayerSelectionStore,
    IClientChatTransport, IClientChatPresentation, IClientWorldMarkerTransport, IClientWorldSynchronizationRuntime,
    IClientWorldSynchronizationSession, IClientWorldSynchronizationPresentation, IClientColonyRegistrationRuntime,
    IClientColonyRegistrationSession, IClientColonyRegistrationTransport, IClientColonyAppearanceStore, IClientSessionStreamTransport,
    IClientShopCatalogTransport, IClientShopPresentation
{
    internal readonly EventModuleTestPorts Ports = new();
    internal readonly ClientSessionLifecycle Session;
    internal readonly ClientSynchronizationCoordinator Sync = new();
    internal readonly ClientEventInbox Inbox = new();
    internal readonly ClientWorldPublicationTestPorts World;
    internal readonly ClientSessionFollowups Module;
    internal readonly ClientOperationFollowups Operations;
    internal readonly ClientEventRefresh Events;
    internal readonly ConcurrentQueue<string> Calls = new();
    internal readonly List<string> Keys = new();
    internal Action<string>? Hook;
    internal long ChatAfter; internal int ChatLimit, SiteCount, ExtensionCount;
    internal bool Suppressed, RegistrationRequested;
    internal ModColonyAppearanceDto? SiteAppearance;
    internal string? Reason;
    private readonly Func<bool> current;
    internal ClientFollowupTestPorts(ClientSessionFollowupSource source)
    {
        ClientSessionWork.Reset(); var connection = new ClientSessionConnection(this);
        Session = new(Ports, Ports, connection); Session.PublishLogin(new() { AuthToken = "auth", SessionId = "session" });
        World = new(Session, Ports);
        var owner = Session.CaptureOwnership(); var context = Ports.ReadContext(); object game = Ports.GameOwner; string world = World.WorldId;
        current = () => Session.Owns(owner, context, source != ClientSessionFollowupSource.ManualLogin) && ReferenceEquals(game, Ports.GameOwner) && world == World.WorldId;
        var mutations = new ClientLocalMutationCoordinator();
        var reader = new ClientEventReader(Session, Inbox, this, Ports); var state = new ClientEventState(reader, this);
        var events = Events = new ClientEventRefresh(Session, reader, Inbox, Sync, mutations, null!, null!, this, state);
        var players = new ClientPlayerDirectory(Session, Ports, Sync, mutations, this, this, this, null!);
        var registration = new ClientColonyRegistration(Session, World, mutations, this, this, World.Flow);
        registration.Diagnostic += (message, _) => { if (message.Contains("refresh requested", StringComparison.Ordinal)) RegistrationRequested = true; };
        var worldSync = new ClientWorldSynchronization(Session, World, mutations, World.Markers, this, this, this, null!);
        Operations = new(events, new ClientShopCatalog(new ClientShopState(Session, Ports, this), mutations, this, this, () => false), players, worldSync, this);
        Module = new(events, players, new ClientChat(Session, this, this), registration, new ClientColonyAppearance(Session, this, World),
            worldSync,
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
    public string Text(string key, params (string Name, object? Value)[] args) { Hook?.Invoke("domain-text"); return key; }
    public string Format(ClientEventRefreshNotice notice) { if (notice.Stage == ClientEventRefreshStage.Started) Reason = notice.Reason; if (notice.Stage == ClientEventRefreshStage.Started) Hook?.Invoke("event-start"); return notice.Stage.ToString(); }
    public void Diagnose(ClientEventRefreshNotice notice) { }
    public void Diagnose(Exception error) { }
    public void Diagnose(string error) { }
    public string MergedReason { get { Hook?.Invoke("merged-text"); return "merged"; } }
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
    public string NotAdministrator => "not-admin";
    public void PreparePreviews(IReadOnlyList<ModServerShopListingDto> listings) => throw new NotSupportedException();
    public Task HydrateAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<ModServerShopListingDto> listings, CancellationToken token) => throw new NotSupportedException();
    Task<ClashOfRimClientNetworkResult<ModListServerShopResponseDto>> IClientShopCatalogTransport.ListAsync(ClashOfRimClientNetworkContext context, CancellationToken token) => Sent<ModListServerShopResponseDto>("Shop", context);
    public Task<ClashOfRimClientNetworkResult<ModUpsertServerShopListingResponseDto>> UpsertAsync(ClashOfRimClientNetworkContext context, ClientShopListingEdit edit, CancellationToken token) => throw new NotSupportedException();
    public Task<ClashOfRimClientNetworkResult<ModRemoveServerShopListingResponseDto>> RemoveAsync(ClashOfRimClientNetworkContext context, string listingId, CancellationToken token) => throw new NotSupportedException();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
