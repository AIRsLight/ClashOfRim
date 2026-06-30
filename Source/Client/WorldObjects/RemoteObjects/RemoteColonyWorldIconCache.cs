using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal static class RemoteColonyWorldIconCache
{
    private const int TextureSize = 96;
    private const int OutlineRadius = 4;
    private const string SettlementTexturePath = "World/WorldObjects/DefaultSettlement";
    private static readonly Dictionary<string, Texture2D> texturesByKey = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Texture2D> settlementTexturesByKey = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Material> settlementMaterialsByKey = new(StringComparer.Ordinal);

    public static void Clear()
    {
        foreach (Texture2D texture in texturesByKey.Values)
        {
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
            }
        }

        foreach (Texture2D texture in settlementTexturesByKey.Values)
        {
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
            }
        }

        texturesByKey.Clear();
        settlementTexturesByKey.Clear();
        settlementMaterialsByKey.Clear();
    }

    public static Texture2D GetTexture(
        string? mode,
        string? iconDefName,
        string? colorDefName,
        string? colorHex,
        string? relationKind)
    {
        string key = BuildKey(mode, iconDefName, colorDefName, colorHex, relationKind);
        if (texturesByKey.TryGetValue(key, out Texture2D texture) && texture != null)
        {
            return texture;
        }

        Texture2D generated = GenerateTexture(mode, iconDefName, colorDefName, colorHex, relationKind);
        texturesByKey[key] = generated;
        return generated;
    }

    public static Material GetSettlementMaterial(string? colorDefName, string? colorHex, string? relationKind)
    {
        string key = BuildSettlementKey(colorDefName, colorHex, relationKind);
        if (settlementMaterialsByKey.TryGetValue(key, out Material material) && material != null)
        {
            return material;
        }

        Material generated = MaterialPool.MatFrom(
            GetSettlementTexture(colorDefName, colorHex, relationKind),
            ShaderDatabase.WorldOverlayTransparentLit,
            Color.white,
            3550);
        settlementMaterialsByKey[key] = generated;
        return generated;
    }

    private static Texture2D GetSettlementTexture(string? colorDefName, string? colorHex, string? relationKind)
    {
        string key = BuildSettlementKey(colorDefName, colorHex, relationKind);
        if (settlementTexturesByKey.TryGetValue(key, out Texture2D texture) && texture != null)
        {
            return texture;
        }

        Texture2D generated = GenerateTexture(
            "Settlement",
            SettlementTexturePath,
            ResolveFillColor(colorDefName, colorHex),
            ResolveOutlineColor(relationKind),
            key);
        settlementTexturesByKey[key] = generated;
        return generated;
    }

    private static Texture2D GenerateTexture(
        string? mode,
        string? iconDefName,
        string? colorDefName,
        string? colorHex,
        string? relationKind)
    {
        try
        {
            Texture2D source = ResolveSourceTexture(mode, iconDefName);
            return GenerateTexture(
                "RemoteColonyIcon",
                source,
                ResolveFillColor(colorDefName, colorHex),
                ResolveOutlineColor(relationKind),
                BuildKey(mode, iconDefName, colorDefName, colorHex, relationKind));
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to generate remote colony icon, using fallback: " + ex.Message);
            return ContentFinder<Texture2D>.Get("World/WorldObjects/Expanding/Town", reportFailure: false)
                ?? BaseContent.BadTex;
        }
    }

    private static Texture2D GenerateTexture(
        string namePrefix,
        string sourcePath,
        Color fillColor,
        Color? outlineColor,
        string key)
    {
        Texture2D source = ContentFinder<Texture2D>.Get(sourcePath, reportFailure: false) ?? BaseContent.BadTex;
        return GenerateTexture(namePrefix, source, fillColor, outlineColor, key);
    }

    private static Texture2D GenerateTexture(
        string namePrefix,
        Texture2D source,
        Color fillColor,
        Color? outlineColor,
        string key)
    {
        Color32[] sourcePixels = ReadSourcePixels(source);
        Color32[] output = new Color32[TextureSize * TextureSize];
        bool[] alphaMask = new bool[TextureSize * TextureSize];

        for (int i = 0; i < sourcePixels.Length; i++)
        {
            alphaMask[i] = sourcePixels[i].a > 20;
        }

        if (outlineColor.HasValue)
        {
            Color32 outline = outlineColor.Value;
            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    if (!HasNearbyAlpha(alphaMask, x, y, OutlineRadius))
                    {
                        continue;
                    }

                    int index = y * TextureSize + x;
                    output[index] = outline;
                }
            }
        }

        Color32 fill = fillColor;
        for (int i = 0; i < sourcePixels.Length; i++)
        {
            Color32 pixel = sourcePixels[i];
            if (pixel.a <= 20)
            {
                continue;
            }

            float brightness = Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b)) / 255f;
            byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(fill.r * Mathf.Lerp(0.45f, 1.12f, brightness)), 0, 255);
            byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(fill.g * Mathf.Lerp(0.45f, 1.12f, brightness)), 0, 255);
            byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(fill.b * Mathf.Lerp(0.45f, 1.12f, brightness)), 0, 255);
            output[i] = new Color32(r, g, b, pixel.a);
        }

        Texture2D texture = new(TextureSize, TextureSize, TextureFormat.ARGB32, mipChain: false)
        {
            name = "ClashOfRim_" + namePrefix + "_" + key,
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixels32(output);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }

    private static Texture2D ResolveSourceTexture(string? mode, string? iconDefName)
    {
        if (string.Equals(mode, "Ideology", StringComparison.Ordinal)
            && ModsConfig.IdeologyActive
            && !string.IsNullOrWhiteSpace(iconDefName))
        {
            IdeoIconDef? iconDef = DefDatabase<IdeoIconDef>.GetNamedSilentFail(iconDefName!);
            if (iconDef?.Icon != null)
            {
                return iconDef.Icon;
            }
        }

        return ContentFinder<Texture2D>.Get("World/WorldObjects/Expanding/Town", reportFailure: false)
            ?? BaseContent.BadTex;
    }

    private static Color ResolveFillColor(string? colorDefName, string? colorHex)
    {
        if (!string.IsNullOrWhiteSpace(colorDefName))
        {
            ColorDef? colorDef = DefDatabase<ColorDef>.GetNamedSilentFail(colorDefName!);
            if (colorDef is not null)
            {
                return colorDef.color;
            }
        }

        if (!string.IsNullOrWhiteSpace(colorHex)
            && ColorUtility.TryParseHtmlString(colorHex, out Color parsed))
        {
            return parsed;
        }

        return new Color(0.78f, 0.74f, 0.62f, 1f);
    }

    private static Color? ResolveOutlineColor(string? relationKind)
    {
        return relationKind switch
        {
            "Ally" => new Color(0.22f, 0.88f, 0.36f, 1f),
            "Hostile" => new Color(0.95f, 0.18f, 0.14f, 1f),
            _ => null
        };
    }

    private static Color32[] ReadSourcePixels(Texture2D source)
    {
        RenderTexture renderTexture = RenderTexture.GetTemporary(TextureSize, TextureSize, 0, RenderTextureFormat.ARGB32);
        RenderTexture? previous = RenderTexture.active;
        try
        {
            Graphics.Blit(source, renderTexture);
            RenderTexture.active = renderTexture;
            Texture2D readable = new(TextureSize, TextureSize, TextureFormat.ARGB32, mipChain: false);
            readable.ReadPixels(new Rect(0f, 0f, TextureSize, TextureSize), 0, 0);
            readable.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            Color32[] pixels = readable.GetPixels32();
            UnityEngine.Object.Destroy(readable);
            return pixels;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(renderTexture);
        }
    }

    private static bool HasNearbyAlpha(bool[] alphaMask, int x, int y, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            int yy = y + dy;
            if (yy < 0 || yy >= TextureSize)
            {
                continue;
            }

            for (int dx = -radius; dx <= radius; dx++)
            {
                int xx = x + dx;
                if (xx < 0 || xx >= TextureSize || dx * dx + dy * dy > radius * radius)
                {
                    continue;
                }

                if (alphaMask[yy * TextureSize + xx])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string BuildKey(
        string? mode,
        string? iconDefName,
        string? colorDefName,
        string? colorHex,
        string? relationKind)
    {
        return (mode ?? string.Empty) + "|"
            + (iconDefName ?? string.Empty) + "|"
            + (colorDefName ?? string.Empty) + "|"
            + (colorHex ?? string.Empty) + "|"
            + (relationKind ?? string.Empty);
    }

    private static string BuildSettlementKey(string? colorDefName, string? colorHex, string? relationKind)
    {
        return (colorDefName ?? string.Empty) + "|"
            + (colorHex ?? string.Empty) + "|"
            + (relationKind ?? string.Empty);
    }
}
