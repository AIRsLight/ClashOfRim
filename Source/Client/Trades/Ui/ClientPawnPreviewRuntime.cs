using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Modules.Trades;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal sealed class ClientPawnPreviewRuntime : IClientPawnPreviewRuntime
{
    public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.Enqueue(action, discard);
    public void Release(IReadOnlyList<IDisposable> resources)
    {
        void Clean()
        {
            foreach (var resource in resources)
                try { resource.Dispose(); }
                catch (Exception ex) { Log.Warning("[ClashOfRim] Pawn preview cache release failed: " + ex); }
        }
        // Cleanup must run on the engine thread even after the old owner expires.
        ClientMainThreadDispatcher.EnqueueSession(Clean, Clean);
    }
    public IClientPawnPreviewTexture? Texture(byte[] png)
    {
        var texture = new Texture2D(2, 2, TextureFormat.ARGB32, mipChain: false);
        try
        {
            if (!texture.LoadImage(png, markNonReadable: true) || texture.width <= 0 || texture.height <= 0
                || texture.width > 128 || texture.height > 128) { UnityEngine.Object.Destroy(texture); return null; }
            texture.name = "ClashOfRim_PawnPreview";
            return new Image(texture);
        }
        catch { UnityEngine.Object.Destroy(texture); throw; }
    }
    public IClientPawnPreviewCard? Restore(ModPawnExchangePackageDto package, out string message)
        => PawnPreviewScribeRestorer.TryRestore(package, out Pawn? pawn, out message) && pawn is not null ? new Card(pawn) : null;
    public void Show(IClientPawnPreviewCard card)
    { if (((Card)card).Value is { } pawn) Find.WindowStack.Add(new Dialog_InfoCard(pawn)); }
    public string Text(string key, params (string Name, object? Value)[] args)
        => ClashOfRimText.Key(key, args.Select(a => (a.Value ?? "").Named(a.Name)).ToArray());
    public void Loading() => Messages.Message(Text("ClashOfRim.Trade.PawnDetailsLoading"), MessageTypeDefOf.NeutralEvent, historical: false);
    public void Failure(string message) => Messages.Message(Text("ClashOfRim.Trade.PawnDetailsFailed", ("MESSAGE", message)), MessageTypeDefOf.RejectInput, historical: false);

    internal sealed class Image : IClientPawnPreviewTexture
    {
        internal Texture2D? Value { get; private set; }
        internal Image(Texture2D value) => Value = value;
        public void Dispose() { var old = Value; Value = null; if (old is not null) UnityEngine.Object.Destroy(old); }
    }
    private sealed class Card : IClientPawnPreviewCard
    {
        internal Pawn? Value { get; private set; }
        internal Card(Pawn value) => Value = value;
        // Drop only the cache reference. An already-open vanilla info card still
        // owns this detached pawn. It is never committed to a world registry.
        public void Dispose() => Value = null;
    }
}
