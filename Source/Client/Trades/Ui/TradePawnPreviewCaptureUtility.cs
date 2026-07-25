using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal static class TradePawnPreviewCaptureUtility
{
    public const int PreviewSize = 96;
    private const int MaximumPreviewBytes = 64 * 1024;

    public static byte[]? TryCapture(Pawn pawn)
    {
        if (pawn is null)
        {
            return null;
        }

        Texture2D? readable = null;
        RenderTexture? previousActive = RenderTexture.active;
        try
        {
            PawnRenderer? renderer = pawn.Drawer?.renderer;
            renderer?.SetAllGraphicsDirty();
            renderer?.EnsureGraphicsInitialized();

            RenderTexture portrait = PortraitsCache.Get(
                pawn,
                new Vector2(PreviewSize, PreviewSize),
                Rot4.South,
                default,
                1f,
                supersample: false,
                compensateForUIScale: false,
                renderHeadgear: true,
                renderClothes: true);
            if (portrait is null || portrait.width <= 0 || portrait.height <= 0)
            {
                return null;
            }

            RenderTexture.active = portrait;
            readable = new Texture2D(
                portrait.width,
                portrait.height,
                TextureFormat.ARGB32,
                mipChain: false);
            readable.ReadPixels(
                new Rect(0f, 0f, portrait.width, portrait.height),
                0,
                0,
                recalculateMipMaps: false);
            readable.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            byte[] png = readable.EncodeToPNG();
            return png.Length is > 0 and <= MaximumPreviewBytes
                ? png
                : null;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to capture trade pawn preview for "
                + (pawn.LabelShortCap ?? pawn.ThingID ?? "<unknown>")
                + ": "
                + ex.GetType().Name
                + " "
                + ex.Message);
            return null;
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (readable is not null)
            {
                UnityEngine.Object.Destroy(readable);
            }
        }
    }
}
