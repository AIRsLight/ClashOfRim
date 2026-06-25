using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

public static partial class RemoteIdeoMaterializer
{
    private static Ideo? TryCreateIdeo(ModWorldIdeoSummaryDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.SavedIdeoPackageXml))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(dto.SavedIdeoPackageSha256)
            && !string.Equals(
                ComputeSha256Hex(dto.SavedIdeoPackageXml!),
                dto.SavedIdeoPackageSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning($"[ClashOfRim] Skipped remote ideo package with mismatched hash {dto.GlobalKey}.");
            return null;
        }

        Ideo? loaded = TryLoadSavedIdeoPackage(dto);
        if (loaded is null)
        {
            return null;
        }

        Ideo ideo = IdeoGenerator.InitLoadedIdeo(loaded);
        ApplyShadowFields(ideo, dto);
        ideo.SortStyleCategories();
        ideo.RecachePrecepts();
        return ideo;
    }

    private static Ideo? TryLoadSavedIdeoPackage(ModWorldIdeoSummaryDto dto)
    {
        string? path = null;
        try
        {
            string directory = Path.Combine(Path.GetTempPath(), "ClashOfRim", "RemoteIdeos");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "remote-ideo-" + Guid.NewGuid().ToString("N") + ".ideo");
            File.WriteAllText(path, dto.SavedIdeoPackageXml!, Encoding.UTF8);
            if (!GameDataSaveLoader.TryLoadIdeo(path, out Ideo loaded))
            {
                return null;
            }

            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Warning($"[ClashOfRim] Failed to load remote ideo package {dto.GlobalKey}: {ex}");
            return null;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                TryDelete(path!);
            }
        }
    }

    private static string ComputeSha256Hex(string text)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
