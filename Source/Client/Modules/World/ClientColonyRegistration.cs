using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal enum ClientColonyRegistrationKind { Sites, Extensions }
internal sealed record ClientColonyRegistrationChange(string Reason, ModColonyAppearanceDto? Appearance = null);
internal interface IClientColonyRegistrationRuntime { IClientColonyRegistrationSession Bind(); }
internal interface IClientColonyRegistrationSession
{
    bool IsCurrent { get; }
    bool Playing { get; }
    bool HasWorld { get; }
    bool HasWorldObjects { get; }
    bool HasColonyContext { get; }
    bool HasExtensionCollectors { get; }
    bool HasRemoteSession { get; }
    IReadOnlyCollection<ModPlayerColonySiteDto> CaptureSites(string user, string colony, ModColonyAppearanceDto? appearance);
    IReadOnlyCollection<ModWorldConfigurationExtensionDto> CaptureExtensions(string user, string colony, string snapshot);
    void Enqueue(Action action, Action discard);
}

// Both modes write the same server catalog. One request owns capture, HTTP and
// publication; forced refreshes coalesce while busy and keep their invalidation.
internal sealed class ClientColonyRegistration
{
    private readonly ClientRequestTracker<Boundary> requests;
    private readonly ClientSessionLifecycle session;
    private readonly IClientWorldIdentity identity;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly IClientColonyRegistrationTransport transport;
    private readonly ClientWorldPublication publication;
    private readonly string?[] accepted = new string?[2];
    private readonly object[] revisions = { new(), new() };
    private readonly Pending?[] pending = new Pending?[2];
    private ClientColonyRegistrationKind? activeKind;
    private bool suppressed, muteNext;
    private int nextSitePoll;
    internal event Action<string, bool>? Diagnostic;
    internal ClientColonyRegistration(ClientSessionLifecycle session, IClientWorldIdentity identity, ClientLocalMutationCoordinator mutations,
        IClientColonyRegistrationRuntime runtime, IClientColonyRegistrationTransport transport, ClientWorldPublication publication)
    {
        this.session = session; this.identity = identity; this.mutations = mutations; this.transport = transport; this.publication = publication;
        requests = new(session, () => new(runtime.Bind(), identity.WorldId), bound => bound.Runtime.IsCurrent
            && (identity.WorldId == bound.WorldId || identity.WorldId == bound.PublishingWorldId), Clear);
    }
    internal bool Suppressed { get => requests.Read(() => suppressed); set => requests.Read(() => suppressed = value); }
    internal bool Busy => requests.Busy;
    internal bool CanStart(ClientColonyRegistrationKind kind) => requests.Read(() => !requests.Busy && Eligible(kind, requests.Capture()));
    internal void Reset() => requests.Reset();
    internal void Invalidate(ClientColonyRegistrationKind kind) => requests.Read(() => { int i = (int)kind; accepted[i] = null; revisions[i] = new(); return true; });
    private void Clear()
    {
        for (int i = 0; i < 2; i++) { accepted[i] = null; revisions[i] = new(); pending[i] = null; }
        activeKind = null; suppressed = muteNext = false; nextSitePoll = 0;
    }
    private bool Eligible(ClientColonyRegistrationKind kind, ClientRequestTracker<Boundary>.Scope scope)
    {
        var runtime = scope.Boundary.Runtime; var context = session.ReadContext();
        if (session.IsUnavailable || !context.IsConfigured || string.IsNullOrWhiteSpace(session.SessionId)
            || string.IsNullOrWhiteSpace(context.CurrentSnapshotId) || !Unblocked(kind) || !runtime.Playing || runtime.HasRemoteSession) return false;
        return kind == ClientColonyRegistrationKind.Sites
            ? runtime.HasWorldObjects && runtime.HasColonyContext
            : runtime.HasWorld && runtime.HasExtensionCollectors;
    }
    private bool Unblocked(ClientColonyRegistrationKind kind) => kind == ClientColonyRegistrationKind.Sites
        ? !suppressed && !mutations.IsPending : !mutations.BlocksGameLifecycle;
    internal Task PollSitesAsync(int ticks, Func<ModColonyAppearanceDto?> captureAppearance)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        if (ticks < nextSitePoll) return Task.CompletedTask;
        nextSitePoll = ticks + 10000;
        if (requests.Busy || !Eligible(ClientColonyRegistrationKind.Sites, scope) || !requests.TryApply(scope, static () => { })) return Task.CompletedTask;
        var appearance = captureAppearance();
        return requests.TryApply(scope, static () => { }) ? StartAsync(ClientColonyRegistrationKind.Sites, appearance, pollOnly: true) : Task.CompletedTask;
    }
    internal Task StartAsync(ClientColonyRegistrationKind kind, ModColonyAppearanceDto? appearance = null)
        => StartAsync(kind, appearance, pollOnly: false);
    // A combined followup captures extensions once. If sites are unavailable
    // (for example during relocation), their gate must not suppress extensions.
    internal Task StartCatalogAsync(ModColonyAppearanceDto? appearance = null)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        var kind = Eligible(ClientColonyRegistrationKind.Sites, scope) ? ClientColonyRegistrationKind.Sites : ClientColonyRegistrationKind.Extensions;
        return requests.TryApply(scope, static () => { }) ? StartAsync(kind, appearance) : Task.CompletedTask;
    }
    private Task StartAsync(ClientColonyRegistrationKind kind, ModColonyAppearanceDto? appearance, bool pollOnly)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        if (!Eligible(kind, scope) || !requests.TryApply(scope, static () => { })) return Task.CompletedTask;
        var input = new Pending(kind, appearance is null ? null : ClientProtocolJson.Copy(appearance), pollOnly);
        if (requests.Busy)
        {
            if (activeKind != kind) pending[(int)kind] ??= input;
            return Task.CompletedTask;
        }
        input = pending[(int)kind] ?? input; pending[(int)kind] = null;
        var request = requests.Begin()!; activeKind = kind;
        return ExecuteAsync(request, input);
    }
    internal Task RequestAsync(ClientColonyRegistrationKind kind, string reason, ModColonyAppearanceDto? appearance = null, bool suppressNotification = false)
    {
        using var publication = ClientSessionWork.EnterPublication();
        return RequestAsync(requests.Capture(), kind, reason, appearance, suppressNotification);
    }
    // Engine events must not queue intermediate sites while relocation or a
    // compatibility callback suppresses publication. Capture under the original
    // scope, since compatibility and appearance reads can change the session.
    internal Task NotifyAsync(ClientColonyRegistrationKind kind, Func<ClientColonyRegistrationChange?> capture)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        if (kind == ClientColonyRegistrationKind.Sites && suppressed) return Task.CompletedTask;
        var change = capture();
        if (change is null || !requests.TryApply(scope, static () => { }) || kind == ClientColonyRegistrationKind.Sites && suppressed) return Task.CompletedTask;
        return RequestAsync(scope, kind, change.Reason, change.Appearance, false);
    }
    private Task RequestAsync(ClientRequestTracker<Boundary>.Scope scope, ClientColonyRegistrationKind kind, string reason,
        ModColonyAppearanceDto? appearance, bool suppressNotification)
    {
        Invalidate(kind);
        if (kind == ClientColonyRegistrationKind.Sites) muteNext |= suppressNotification;
        pending[(int)kind] = new(kind, appearance is null ? null : ClientProtocolJson.Copy(appearance));
        requests.TryApply(scope, () => Diagnostic?.Invoke("[ClashOfRim] Colony " + kind + " refresh requested: " + reason, false));
        return DispatchAsync(scope);
    }
    private async Task DispatchAsync(ClientRequestTracker<Boundary>.Scope scope)
    {
        Task? work = null; bool consumed = false;
        await ClientGamePublication.EnqueueAsync((action, discard) => scope.Boundary.Runtime.Enqueue(action, () => { consumed = true; discard(); }),
            scope.Owner.Token, action => requests.TryApply(scope, action), () =>
            {
                if (consumed) return; consumed = true;
                if (requests.Busy) return;
                foreach (var input in pending)
                    if (input is not null && Eligible(input.Kind, scope)) { work = StartAsync(input.Kind, input.Appearance); break; }
            }).ConfigureAwait(false);
        if (work is not null) await work.ConfigureAwait(false);
    }
    private async Task ExecuteAsync(ClientRequestTracker<Boundary>.Request request, Pending input)
    {
        var scope = request.Scope; var bound = scope.Boundary; var runtime = bound.Runtime; var context = request.Context;
        var observation = publication.Capture();
        object siteRevision = revisions[0], extensionRevision = revisions[1];
        bool Current()
        {
            bool current = false;
            requests.TryApply(request, () => current = !session.IsUnavailable && Unblocked(input.Kind));
            return current;
        }
        // Availability can enumerate live maps. Only capture and game publication
        // call Ready; HTTP continuations use the thread-safe ownership check above.
        bool Ready() => Current() && Eligible(input.Kind, scope) && Current();
        bool Step(Action action) => Ready() && requests.TryApply(request, action) && Ready();
        void Report(string message, bool warning = false) { if (Current()) requests.TryApply(request, () => Diagnostic?.Invoke(message, warning)); }
        try
        {
            var sites = Array.Empty<ModPlayerColonySiteDto>();
            if (input.Kind == ClientColonyRegistrationKind.Sites
                && !Step(() => sites = ClientProtocolJson.Copy(runtime.CaptureSites(context.UserId, context.ColonyId, input.Appearance).ToArray()))) return;
            var extensions = Array.Empty<ModWorldConfigurationExtensionDto>();
            // Periodic location checks must not export every ideology. Explicit site
            // events, entry, saves and reform notifications still collect live extensions.
            if (!input.PollOnly && !Step(() => extensions = ClientProtocolJson.Copy(runtime.CaptureExtensions(context.UserId, context.ColonyId, context.CurrentSnapshotId ?? "unknown").ToArray()))) return;
            string siteSignature = Signature(sites), extensionSignature = Signature(extensions);
            if (sites.Length == 0 && extensions.Length == 0) return;
            if ((input.PollOnly || accepted[1] == extensionSignature) && (input.Kind == ClientColonyRegistrationKind.Extensions || accepted[0] == siteSignature)) return;
            bool mute = input.Kind == ClientColonyRegistrationKind.Sites && muteNext;
            if (input.Kind == ClientColonyRegistrationKind.Sites) muteNext = false;
            await ClientSessionWork.Run(async () =>
            {
                if (!Current()) return;
                var result = await transport.RegisterAsync(context, sites, extensions, mute, scope.Owner.Token).ConfigureAwait(false);
                if (!Current()) return;
                if (!result.Success || result.Response is null)
                { Report("[ClashOfRim] Colony " + input.Kind + " registration failed: " + result.ErrorCode + " " + result.Message, true); return; }
                var response = ClientProtocolJson.Copy(result.Response);
                if (response.Result?.Accepted != true)
                { Report("[ClashOfRim] Colony " + input.Kind + " registration rejected: " + response.Result?.ErrorCode + " " + response.Result?.Message, true); return; }
                bool consumed = false;
                await ClientGamePublication.EnqueueAsync((action, discard) => runtime.Enqueue(action, () => { consumed = true; discard(); }),
                    scope.Owner.Token, action => requests.TryApply(request, action), () =>
                    {
                        if (consumed || !Ready()) return; consumed = true;
                        string world = string.IsNullOrWhiteSpace(response.WorldConfiguration?.WorldConfigurationId) ? bound.WorldId : response.WorldConfiguration!.WorldConfigurationId.Trim();
                        string source = input.Kind == ClientColonyRegistrationKind.Sites ? "player-colony-site-registration" : "world-configuration-extension-sync";
                        bound.PublishingWorldId = world;
                        try
                        {
                            if (!publication.PublishOwned(observation, response.WorldConfiguration, input.PollOnly ? ClientWorldProjection.Sites : ClientWorldProjection.Catalog, Ready, (source, response.WorldMapMarkers))) return;
                            if (identity.WorldId != world) return;
                            if (bound.WorldId != world) { accepted[0] = accepted[1] = null; bound.WorldId = world; }
                        }
                        finally { bound.PublishingWorldId = null; }
                        if (input.Kind == ClientColonyRegistrationKind.Sites && ReferenceEquals(revisions[0], siteRevision)) accepted[0] = siteSignature;
                        if (!input.PollOnly && ReferenceEquals(revisions[1], extensionRevision)) accepted[1] = extensionSignature;
                        Report("[ClashOfRim] Colony " + input.Kind + " registered: sites=" + response.AcceptedCount + ", extensions=" + extensions.Length);
                    }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { Report("[ClashOfRim] Colony " + input.Kind + " registration exception: " + ex, true); }
        finally
        {
            bool owned = requests.TryApply(request, () => activeKind = null);
            requests.End(request);
            if (owned && pending.Any(item => item is not null)) await DispatchAsync(scope).ConfigureAwait(false);
        }
    }
    // Structured, ordinal fingerprints preserve field boundaries, duplicate entries
    // and every DTO field while ignoring input enumeration order and UI culture.
    internal static string Signature<T>(IEnumerable<T> values)
        => ClientProtocolJson.UnorderedSignature(values);
    private sealed record Pending(ClientColonyRegistrationKind Kind, ModColonyAppearanceDto? Appearance, bool PollOnly = false);
    private sealed class Boundary
    {
        internal readonly IClientColonyRegistrationSession Runtime;
        internal string WorldId;
        internal string? PublishingWorldId;
        internal Boundary(IClientColonyRegistrationSession runtime, string world) { Runtime = runtime; WorldId = world; }
    }
}
