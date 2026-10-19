using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;

internal static class BiotechSavedXenotypePackages
{
    private const int MaxXenotypeXmlBytes = 1024 * 1024;

    public static BiotechSavedXenotypePackageDto? BuildPackage(Pawn pawn)
    {
        if (pawn.genes?.CustomXenotype is not CustomXenotype customXenotype)
        {
            return null;
        }

        string? xml = TryExportXenotypePackageXml(customXenotype);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        string packageXml = xml!;
        return new BiotechSavedXenotypePackageDto
        {
            Name = customXenotype.name,
            Xml = packageXml,
            XmlSha256 = ComputeSha256Hex(packageXml)
        };
    }

    public static bool TryRegister(BiotechSavedXenotypePackageDto? package, out string message)
    {
        message = string.Empty;
        if (package is null || string.IsNullOrWhiteSpace(package.Xml))
        {
            return true;
        }

        if (Current.Game is null)
        {
            message = ClashOfRimText.Key("ClashOfRim.Xenotype.StatusGameMissing");
            return false;
        }

        if (!TryRead(package, out CustomXenotype? loaded, out message)) return false;
        if (loaded is null) return true;

        Current.Game.customXenotypeDatabase ??= new CustomXenotypeDatabase();
        if (Current.Game.customXenotypeDatabase.customXenotypes.Any(existing => SameXenotype(existing, loaded)))
        {
            message = ClashOfRimText.Key("ClashOfRim.Xenotype.StatusAlreadyExists");
            return true;
        }

        Current.Game.customXenotypeDatabase.customXenotypes.Add(loaded);
        message = ClashOfRimText.Key("ClashOfRim.Xenotype.StatusRegistered");
        return true;
    }

    // Loading a custom definition is independent of registering it in a save.
    internal static bool TryRead(BiotechSavedXenotypePackageDto? package, out CustomXenotype? loaded, out string message)
    {
        loaded = null;
        message = string.Empty;
        if (package is null || string.IsNullOrWhiteSpace(package.Xml)) return true;
        if (Encoding.UTF8.GetByteCount(package.Xml) > MaxXenotypeXmlBytes)
        {
            message = ClashOfRimText.Key("ClashOfRim.Xenotype.StatusXmlTooLarge");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(package.XmlSha256)
            && !string.Equals(ComputeSha256Hex(package.Xml), package.XmlSha256, StringComparison.OrdinalIgnoreCase))
        {
            message = ClashOfRimText.Key("ClashOfRim.Xenotype.StatusHashMismatch");
            return false;
        }

        // The native helper force-stops Scribe on errors, so reject a foreign
        // active operation before entering it (including from preview callbacks).
        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            message = ClashOfRimText.Key("ClashOfRim.Xenotype.StatusLoadFailed",
                nameof(InvalidOperationException).Named("TYPE"), "Scribe is already active.".Named("MESSAGE"));
            return false;
        }
        if (!TryLoadXenotypePackageXml(package.Xml, out loaded, out message))
        {
            return false;
        }

        if (loaded is null)
        {
            message = ClashOfRimText.Key("ClashOfRim.Xenotype.StatusPayloadEmpty");
            return false;
        }

        return true;
    }

    private static string? TryExportXenotypePackageXml(CustomXenotype xenotype)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ClashOfRim", "Xenotypes");
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".xtp");
        try
        {
            Directory.CreateDirectory(directory);
            GameDataSaveLoader.SaveXenotype(xenotype, path);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException)
        {
            Log.Warning("[ClashOfRim] Failed to export custom xenotype package: " + ex);
            return null;
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static bool TryLoadXenotypePackageXml(string xml, out CustomXenotype? xenotype, out string message)
    {
        xenotype = null;
        message = string.Empty;
        string directory = Path.Combine(Path.GetTempPath(), "ClashOfRim", "RemoteXenotypes");
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".xtp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, xml, Encoding.UTF8);
            if (!GameDataSaveLoader.TryLoadXenotype(path, out CustomXenotype loaded))
            {
                message = ClashOfRimText.Key("ClashOfRim.Xenotype.StatusVanillaLoadFailed");
                return false;
            }

            xenotype = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException)
        {
            message = ClashOfRimText.Key(
                "ClashOfRim.Xenotype.StatusLoadFailed",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            return false;
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static bool SameXenotype(CustomXenotype left, CustomXenotype right)
    {
        return string.Equals(left.name, right.name, StringComparison.Ordinal)
            && left.inheritable == right.inheritable
            && string.Equals(left.IconDef?.defName, right.IconDef?.defName, StringComparison.Ordinal)
            && left.genes.Select(gene => gene?.defName)
                .Where(defName => !string.IsNullOrWhiteSpace(defName))
                .OrderBy(defName => defName, StringComparer.Ordinal)
                .SequenceEqual(
                    right.genes.Select(gene => gene?.defName)
                        .Where(defName => !string.IsNullOrWhiteSpace(defName))
                        .OrderBy(defName => defName, StringComparer.Ordinal),
                    StringComparer.Ordinal);
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
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
