using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal static class TradePawnPreviewService
{
    private const int MaximumTextureCacheEntries = 256;
    private const int MaximumDetailCacheEntries = 16;

    private static readonly object Gate = new();
    private static readonly HttpClient NetworkHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private static readonly SemaphoreSlim PreviewDownloadSlots = new(4, 4);
    private static readonly Dictionary<string, Texture2D> TextureCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Pawn> DetailPawnCache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> PreviewRequests = new(StringComparer.Ordinal);
    private static readonly HashSet<string> FailedPreviewRequests = new(StringComparer.Ordinal);
    private static readonly HashSet<string> DetailRequests = new(StringComparer.Ordinal);
    private static int cacheGeneration;

    public static bool TryGetTexture(ModThingReferenceDto reference, out Texture2D? texture)
    {
        texture = null;
        string key = CacheKey(reference);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (Gate)
        {
            return TextureCache.TryGetValue(key, out texture);
        }
    }

    public static void RequestPreview(ModThingReferenceDto reference)
    {
        string? pawnPackageId = reference.PawnPackageId;
        string key = CacheKey(reference);
        if (string.IsNullOrWhiteSpace(pawnPackageId)
            || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        int generation;
        lock (Gate)
        {
            if (TextureCache.ContainsKey(key)
                || PreviewRequests.Contains(key)
                || FailedPreviewRequests.Contains(key))
            {
                return;
            }

            PreviewRequests.Add(key);
            generation = cacheGeneration;
        }

        ClashOfRimClientNetworkContext? context = CreateNetworkContext();
        if (context is null)
        {
            MarkPreviewFailed(key, generation);
            return;
        }

        Task.Run(async () =>
        {
            byte[]? png = null;
            await PreviewDownloadSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                var client = new ClashOfRimModNetworkClient(NetworkHttpClient, context);
                ClashOfRimClientNetworkResult<byte[]> result =
                    await client.GetPawnPackagePreviewAsync(pawnPackageId!).ConfigureAwait(false);
                if (!result.Success || result.Response is not { Length: > 0 })
                {
                    MarkPreviewFailed(key, generation);
                    return;
                }

                png = result.Response;
            }
            catch
            {
                MarkPreviewFailed(key, generation);
                return;
            }
            finally
            {
                PreviewDownloadSlots.Release();
            }

            byte[] capturedPng = png;
            ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                CompletePreviewRequest(key, capturedPng, generation));
        });
    }

    public static void OpenDetails(ModThingReferenceDto reference)
    {
        string key = CacheKey(reference);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        int generation;
        lock (Gate)
        {
            if (DetailPawnCache.TryGetValue(key, out Pawn? cached))
            {
                Find.WindowStack.Add(new Dialog_InfoCard(cached));
                return;
            }

            if (!DetailRequests.Add(key))
            {
                Messages.Message(
                    ClashOfRimText.Key("ClashOfRim.Trade.PawnDetailsLoading"),
                    MessageTypeDefOf.NeutralEvent,
                    historical: false);
                return;
            }

            generation = cacheGeneration;
        }

        Messages.Message(
            ClashOfRimText.Key("ClashOfRim.Trade.PawnDetailsLoading"),
            MessageTypeDefOf.NeutralEvent,
            historical: false);

        if (reference.PawnPackage is not null)
        {
            CompleteDetailsRequest(key, reference.PawnPackage, generation);
            return;
        }

        string? pawnPackageId = reference.PawnPackageId;
        if (string.IsNullOrWhiteSpace(pawnPackageId))
        {
            FailDetailsRequest(
                key,
                ClashOfRimText.Key("ClashOfRim.PawnPackage.DownloadMissingPackage"),
                generation);
            return;
        }

        ClashOfRimClientNetworkContext? context = CreateNetworkContext();
        if (context is null)
        {
            FailDetailsRequest(
                key,
                ClashOfRimText.Key("ClashOfRim.Status.NotConfigured"),
                generation);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                var client = new ClashOfRimModNetworkClient(NetworkHttpClient, context);
                ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto> result =
                    await client.GetPawnPackageAsync(pawnPackageId!).ConfigureAwait(false);
                if (!result.Success || result.Response?.PawnPackage is null)
                {
                    EnqueueDetailsFailure(
                        key,
                        ClashOfRimText.Key(
                            "ClashOfRim.PawnPackage.DownloadFailed",
                            (result.ErrorCode ?? string.Empty).Named("CODE"),
                            (result.Message ?? string.Empty).Named("MESSAGE")),
                        generation);
                    return;
                }

                if (result.Response.Result is { Accepted: false } response)
                {
                    EnqueueDetailsFailure(
                        key,
                        ClashOfRimText.Key(
                            "ClashOfRim.PawnPackage.DownloadRejected",
                            response.ErrorCode.ToString().Named("CODE"),
                            (response.Message ?? string.Empty).Named("MESSAGE")),
                        generation);
                    return;
                }

                ModPawnExchangePackageDto package = result.Response.PawnPackage;
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    CompleteDetailsRequest(key, package, generation));
            }
            catch (Exception ex)
            {
                EnqueueDetailsFailure(
                    key,
                    ClashOfRimText.Key(
                        "ClashOfRim.PawnPackage.DownloadException",
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE")),
                    generation);
            }
        });
    }

    public static void Reset()
    {
        lock (Gate)
        {
            cacheGeneration++;
            foreach (Texture2D texture in TextureCache.Values)
            {
                UnityEngine.Object.Destroy(texture);
            }

            TextureCache.Clear();
            DetailPawnCache.Clear();
            PreviewRequests.Clear();
            FailedPreviewRequests.Clear();
            DetailRequests.Clear();
        }
    }

    private static ClashOfRimClientNetworkContext? CreateNetworkContext()
    {
        ClashOfRimMod? mod = ClashOfRimMod.Instance;
        return mod?.IsNetworkConfigured == true
            ? mod.CreateNetworkContext()
            : null;
    }

    private static void CompletePreviewRequest(string key, byte[] png, int generation)
    {
        Texture2D? texture = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.ARGB32, mipChain: false);
            if (!texture.LoadImage(png, markNonReadable: true)
                || texture.width <= 0
                || texture.height <= 0
                || texture.width > 128
                || texture.height > 128)
            {
                UnityEngine.Object.Destroy(texture);
                MarkPreviewFailed(key, generation);
                return;
            }

            texture.name = "ClashOfRim_PawnPreview_" + key;
            lock (Gate)
            {
                if (generation != cacheGeneration)
                {
                    UnityEngine.Object.Destroy(texture);
                    return;
                }

                PreviewRequests.Remove(key);
                FailedPreviewRequests.Remove(key);
                if (TextureCache.Count >= MaximumTextureCacheEntries)
                {
                    foreach (Texture2D cached in TextureCache.Values)
                    {
                        UnityEngine.Object.Destroy(cached);
                    }

                    TextureCache.Clear();
                }

                TextureCache[key] = texture;
            }
        }
        catch
        {
            if (texture is not null)
            {
                UnityEngine.Object.Destroy(texture);
            }

            MarkPreviewFailed(key, generation);
        }
    }

    private static void CompleteDetailsRequest(
        string key,
        ModPawnExchangePackageDto package,
        int generation)
    {
        lock (Gate)
        {
            if (generation != cacheGeneration)
            {
                return;
            }
        }

        if (!GiftPawnPackageUtility.TryRestoreTradePawn(package, out Pawn? pawn, out string message)
            || pawn is null)
        {
            FailDetailsRequest(key, message, generation);
            return;
        }

        lock (Gate)
        {
            if (generation != cacheGeneration)
            {
                return;
            }

            DetailRequests.Remove(key);
            if (DetailPawnCache.Count >= MaximumDetailCacheEntries)
            {
                DetailPawnCache.Clear();
            }

            DetailPawnCache[key] = pawn;
        }

        Find.WindowStack.Add(new Dialog_InfoCard(pawn));
    }

    private static void EnqueueDetailsFailure(string key, string message, int generation)
    {
        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
            FailDetailsRequest(key, message, generation));
    }

    private static void FailDetailsRequest(string key, string message, int generation)
    {
        lock (Gate)
        {
            if (generation != cacheGeneration)
            {
                return;
            }

            DetailRequests.Remove(key);
        }

        Messages.Message(
            ClashOfRimText.Key(
                "ClashOfRim.Trade.PawnDetailsFailed",
                (message ?? string.Empty).Named("MESSAGE")),
            MessageTypeDefOf.RejectInput,
            historical: false);
    }

    private static void MarkPreviewFailed(string key, int generation)
    {
        lock (Gate)
        {
            if (generation != cacheGeneration)
            {
                return;
            }

            PreviewRequests.Remove(key);
            FailedPreviewRequests.Add(key);
        }
    }

    private static string CacheKey(ModThingReferenceDto reference)
    {
        string packageKey;
        if (!string.IsNullOrWhiteSpace(reference.PawnPackageId))
        {
            packageKey = "id:" + reference.PawnPackageId;
        }
        else
        {
            string? globalId = reference.PawnPackage?.Reference?.GlobalId;
            string? xmlHash = reference.PawnPackage?.Scribe?.XmlSha256;
            packageKey = !string.IsNullOrWhiteSpace(globalId) || !string.IsNullOrWhiteSpace(xmlHash)
                ? "inline:" + (globalId ?? string.Empty) + "\u001f" + (xmlHash ?? string.Empty)
                : string.IsNullOrWhiteSpace(reference.GlobalKey)
                    ? string.Empty
                    : "key:" + reference.GlobalKey;
        }

        if (string.IsNullOrWhiteSpace(packageKey))
        {
            return string.Empty;
        }

        return (ClashOfRimMod.Instance?.ServerBaseUrl ?? string.Empty) + "\u001f" + packageKey;
    }
}
