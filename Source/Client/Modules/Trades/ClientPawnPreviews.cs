using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Trades;

internal interface IClientPawnPreviewTexture : IDisposable { }
internal interface IClientPawnPreviewCard : IDisposable { }
internal interface IClientPawnPreviewTransport
{
    Task<ClashOfRimClientNetworkResult<byte[]>> PreviewAsync(ClashOfRimClientNetworkContext context, string id, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto>> PackageAsync(ClashOfRimClientNetworkContext context, string id, CancellationToken token);
}
internal interface IClientPawnPreviewRuntime
{
    void Enqueue(Action action, Action discard);
    // Release is also safe from a worker: the adapter schedules engine cleanup even
    // when the resource's old session has already expired.
    void Release(IReadOnlyList<IDisposable> resources);
    IClientPawnPreviewTexture? Texture(byte[] png);
    IClientPawnPreviewCard? Restore(ModPawnExchangePackageDto package, out string message);
    void Show(IClientPawnPreviewCard card);
    string Text(string key, params (string Name, object? Value)[] args);
    void Loading();
    void Failure(string message);
}

// Owns request deduplication, bounded caches and publication. Engine adapters own
// decoding/restoration only; they cannot select an account or borrow current settings.
internal sealed class ClientPawnPreviews
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientRequestTracker<(IClientSnapshotGameSession Game, bool Unavailable)> lifetime;
    private readonly IClientPawnPreviewTransport transport;
    private readonly IClientPawnPreviewRuntime runtime;
    private readonly Cache<IClientPawnPreviewTexture> textures;
    private readonly Cache<IClientPawnPreviewCard> cards;
    private readonly SemaphoreSlim slots = new(4, 4);

    internal ClientPawnPreviews(ClientSessionLifecycle session, IClientSnapshotGame game,
        IClientPawnPreviewTransport transport, IClientPawnPreviewRuntime runtime, int textureCapacity = 256, int cardCapacity = 16)
    {
        this.session = session; this.transport = transport; this.runtime = runtime;
        textures = new(textureCapacity, runtime.Release); cards = new(cardCapacity, runtime.Release);
        lifetime = new(session, () => (game.BindCurrentGame(), session.IsUnavailable),
            boundary => boundary.Game.IsCurrent && boundary.Unavailable == session.IsUnavailable,
            () => { textures.Clear(); cards.Clear(); });
    }
    internal void Reset() => lifetime.Reset();
    internal IClientPawnPreviewTexture? ReadTexture(ModThingReferenceDto reference) => lifetime.Read(() =>
        string.IsNullOrWhiteSpace(reference.PawnPackageId) ? null : textures.Read(new(reference.PawnPackageId, null, null)));

    internal Task RequestPreviewAsync(ModThingReferenceDto reference) => lifetime.Read(() =>
    {
        string? id = reference.PawnPackageId;
        if (string.IsNullOrWhiteSpace(id) || !session.ReadContext().IsConfigured || session.IsUnavailable) return Task.CompletedTask;
        var key = new Key(id, null, null);
        if (textures.Read(key) is not null || textures.Failed.Contains(key)) return Task.CompletedTask;
        if (textures.Pending.TryGetValue(key, out var pending)) return pending.Completion.Task;
        var request = Begin(textures, key);
        _ = Run(request, textures, async () =>
        {
            await slots.WaitAsync(request.Owner.Owner.Token).ConfigureAwait(false);
            byte[]? bytes;
            try
            {
                if (!Current(request, textures)) return;
                var result = await transport.PreviewAsync(request.Context, id!, request.Owner.Owner.Token).ConfigureAwait(false);
                if (!Current(request, textures)) return;
                if (!result.Success || result.Response is not { Length: > 0 }) { FailPreview(request); return; }
                bytes = (byte[])result.Response.Clone();
            }
            finally { slots.Release(); }
            await Publish(request, textures, () =>
            {
                var texture = runtime.Texture(bytes);
                if (texture is null) { FailPreview(request); return; }
                if (!Apply(request, textures, () => textures.Store(request, texture))) runtime.Release(new IDisposable[] { texture });
            }).ConfigureAwait(false);
        });
        return request.Completion.Task;
    });

    internal Task OpenDetailsAsync(ModThingReferenceDto reference) => lifetime.Read(() =>
    {
        // Freeze the entire inline package, including metadata and DLC extensions.
        // Global ID/XML hash alone do not identify those independently changing fields.
        string? json = reference.PawnPackage is null ? null : ClientProtocolJson.Serialize(reference.PawnPackage);
        var key = new Key(reference.PawnPackageId,
            json is null ? null : ClientSnapshotPayloadCodec.Hash(Encoding.UTF8.GetBytes(json)),
            json is null && string.IsNullOrWhiteSpace(reference.PawnPackageId) ? reference.GlobalKey : null);
        if (string.IsNullOrWhiteSpace(key.Package) && key.Inline is null && string.IsNullOrWhiteSpace(key.Reference)) return Task.CompletedTask;
        if (cards.Read(key) is { } cached)
        {
            var owner = lifetime.Capture();
            return Publish(owner, action => lifetime.TryApply(owner, action),
                () => { if (ReferenceEquals(cards.Read(key), cached)) runtime.Show(cached); });
        }
        if (cards.Pending.TryGetValue(key, out var pending)) return pending.Completion.Task;
        var request = Begin(cards, key);
        _ = Run(request, cards, async () =>
        {
            await Publish(request, cards, runtime.Loading).ConfigureAwait(false);
            if (!Current(request, cards)) return;
            ModPawnExchangePackageDto? package;
            if (json is not null) package = ClientProtocolJson.Deserialize<ModPawnExchangePackageDto>(json, static type => type);
            else
            {
                if (string.IsNullOrWhiteSpace(key.Package))
                { await FailDetails(request, "ClashOfRim.PawnPackage.DownloadMissingPackage"); return; }
                if (!request.Context.IsConfigured || request.Owner.Boundary.Unavailable)
                { await FailDetails(request, "ClashOfRim.Status.NotConfigured"); return; }
                var result = await transport.PackageAsync(request.Context, key.Package!, request.Owner.Owner.Token).ConfigureAwait(false);
                if (!Current(request, cards)) return;
                if (!result.Success || result.Response?.PawnPackage is null)
                { await FailDetails(request, "ClashOfRim.PawnPackage.DownloadFailed", ("CODE", result.ErrorCode), ("MESSAGE", result.Message)); return; }
                if (result.Response.Result is not { Accepted: true })
                { await FailDetails(request, "ClashOfRim.PawnPackage.DownloadRejected", ("CODE", result.Response.Result?.ErrorCode), ("MESSAGE", result.Response.Result?.Message)); return; }
                package = ClientProtocolJson.Copy(result.Response.PawnPackage);
            }
            await Publish(request, cards, () =>
            {
                var card = runtime.Restore(package, out string message);
                if (card is null) { ReportFailure(request, message); return; }
                if (!Apply(request, cards, () => cards.Store(request, card))) { runtime.Release(new IDisposable[] { card }); return; }
                // Store consumes the request, but the resource still belongs to its original scope.
                lifetime.TryApply(request.Owner, () => runtime.Show(card));
            }).ConfigureAwait(false);
        });
        return request.Completion.Task;
    });

    private Request Begin<T>(Cache<T> cache, Key key) where T : class, IDisposable
    {
        var request = new Request(lifetime.Capture(), session.ReadContext(), key);
        cache.Pending.Add(key, request); return request;
    }
    private bool Apply<T>(Request request, Cache<T> cache, Action action) where T : class, IDisposable
    {
        bool applied = false;
        lifetime.TryApply(request.Owner, () =>
        {
            if (!cache.Pending.TryGetValue(request.Key, out var current) || !ReferenceEquals(current, request)) return;
            action(); applied = true;
        });
        return applied;
    }
    private bool Current<T>(Request request, Cache<T> cache) where T : class, IDisposable => Apply(request, cache, static () => { });
    private Task Publish<T>(Request request, Cache<T> cache, Action action) where T : class, IDisposable
        => Publish(request.Owner, effect => Apply(request, cache, effect), action);
    private Task Publish(ClientRequestTracker<(IClientSnapshotGameSession Game, bool Unavailable)>.Scope owner,
        Func<Action, bool> current, Action action)
    {
        bool published = false;
        return ClientGamePublication.EnqueueAsync(runtime.Enqueue, owner.Owner.Token, current, () =>
        { if (published) return; published = true; action(); });
    }
    private async Task Run<T>(Request request, Cache<T> cache, Func<Task> work) where T : class, IDisposable
    {
        try { await ClientSessionWork.Run(work).ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (ReferenceEquals(cache, textures)) FailPreview(request);
            else
            {
                try { await FailDetails(request, "ClashOfRim.PawnPackage.DownloadException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message)); }
                catch { /* Engine failure reporting cannot leave this request permanently pending. */ }
            }
        }
        finally
        {
            Apply(request, cache, () => cache.Pending.Remove(request.Key));
            request.Completion.TrySetResult();
        }
    }
    private void FailPreview(Request request) => Apply(request, textures, () =>
    { textures.Pending.Remove(request.Key); textures.Failed.Add(request.Key); });
    private Task FailDetails(Request request, string key, params (string Name, object? Value)[] args)
        => Publish(request, cards, () => ReportFailure(request, runtime.Text(key, args)));
    private void ReportFailure(Request request, string message) => Apply(request, cards, () =>
    { cards.Pending.Remove(request.Key); runtime.Failure(message); });

    private sealed record Key(string? Package, string? Inline, string? Reference);
    private sealed record Request(ClientRequestTracker<(IClientSnapshotGameSession Game, bool Unavailable)>.Scope Owner,
        ClashOfRimClientNetworkContext Context, Key Key)
    { internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    // Both channels share cache insertion/eviction and exact pending-request identity.
    private sealed class Cache<T> where T : class, IDisposable
    {
        private readonly int capacity;
        private readonly Action<IReadOnlyList<IDisposable>> release;
        private readonly Dictionary<Key, T> values = new();
        internal readonly Dictionary<Key, Request> Pending = new();
        internal readonly HashSet<Key> Failed = new();
        internal Cache(int capacity, Action<IReadOnlyList<IDisposable>> release) { this.capacity = capacity; this.release = release; }
        internal T? Read(Key key) => values.TryGetValue(key, out var value) ? value : null;
        internal void Store(Request request, T value)
        {
            Pending.Remove(request.Key); Failed.Remove(request.Key);
            var evicted = values.Count >= capacity ? values.Values.Cast<IDisposable>().ToArray() : Array.Empty<IDisposable>();
            if (evicted.Length > 0) values.Clear();
            values[request.Key] = value;
            if (evicted.Length > 0) release(evicted);
        }
        internal void Clear()
        {
            var previous = values.Values.Cast<IDisposable>().ToArray();
            values.Clear(); Pending.Clear(); Failed.Clear();
            if (previous.Length > 0) release(previous);
        }
    }
}
