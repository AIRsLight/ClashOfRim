using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal static class ThingStatePackageUtility
{
    private const int MaxScribeXmlBytes = 1024 * 1024;

    private static readonly HashSet<string> KnownCompTypeNames = new(StringComparer.Ordinal)
    {
        "CompQuality",
        "CompBiocodable",
        "CompUniqueWeapon",
        "CompBladelinkWeapon",
        "CompArt",
        "CompColorable",
        "CompForbiddable",
        "CompStyleable",
        "CompEquippable",
        "CompReloadable",
        "CompApparelReloadable",
        "CompGlower",
        "CompRefuelable",
        "CompBreakdownable",
        "CompPowerTrader",
        "CompProperties_UseEffect",
        "CompUseEffect",
        "CompGeneratedNames",
        "CompRottable",
        "CompDrug",
        "CompIngredients",
        "CompFoodPoisonable"
    };

    public static void TryAttachFallbackPackage(Thing thing, ModThingReferenceDto reference)
    {
        if (!ShouldAttachFallbackPackage(thing, reference))
        {
            return;
        }

        try
        {
            string? xml = TryCreateThingScribeXml(thing);
            if (string.IsNullOrWhiteSpace(xml))
            {
                return;
            }

            int xmlBytes = Encoding.UTF8.GetByteCount(xml!);
            if (xmlBytes <= 0 || xmlBytes > MaxScribeXmlBytes)
            {
                return;
            }

            reference.ThingPackage = new ModThingStatePackageDto
            {
                PackageVersion = 1,
                GlobalKey = reference.GlobalKey,
                DefName = thing.def?.defName,
                Label = thing.LabelCapNoCount,
                StackCount = Math.Max(1, reference.StackCount),
                Scribe = new ModThingScribePayloadDto
                {
                    XmlGzipBase64 = CompressToBase64(xml!),
                    XmlSha256 = ComputeSha256Hex(xml!),
                    UncompressedBytes = xmlBytes
                }
            };
        }
        catch (Exception ex)
        {
            ClashLog.Message("[ClashOfRim][ThingPackage] failed to build fallback package for "
                + (thing.def?.defName ?? "<null>")
                + ": "
                + ex.Message);
        }
    }

    public static bool TryRestore(
        ModThingReferenceDto reference,
        int stackCount,
        out Thing? thing,
        out string? missingDefName)
    {
        thing = null;
        missingDefName = null;
        ModThingStatePackageDto? package = reference.ThingPackage;
        ModThingScribePayloadDto? scribe = package?.Scribe;
        if (package is null || scribe is null || string.IsNullOrWhiteSpace(scribe.XmlGzipBase64))
        {
            return false;
        }

        string? tempFile = null;
        bool initialized = false;
        try
        {
            string xml = DecompressXml(scribe);
            string wrappedXml = "<root>" + xml + "</root>";
            string directory = Path.Combine(Path.GetTempPath(), "ClashOfRim", "ThingStatePackage");
            Directory.CreateDirectory(directory);
            tempFile = Path.Combine(directory, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".xml");
            File.WriteAllText(tempFile, wrappedXml, Encoding.UTF8);

            Scribe.loader.InitLoading(tempFile);
            initialized = true;
            Thing? restored = null;
            Scribe_Deep.Look(ref restored, "thing");
            Scribe.loader.FinalizeLoading();
            initialized = false;
            if (restored is null)
            {
                missingDefName = reference.DefName;
                return false;
            }

            if (!ReferenceDefMatches(reference, restored))
            {
                missingDefName = reference.DefName;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(reference.StuffDefName)
                && !string.Equals(reference.StuffDefName, restored.Stuff?.defName, StringComparison.OrdinalIgnoreCase))
            {
                missingDefName = reference.StuffDefName;
                return false;
            }

            PawnExchangeLoadIdUtility.LocalizeRestoredThing(restored);
            restored.stackCount = Math.Max(1, stackCount);
            thing = restored;
            return true;
        }
        catch (Exception ex)
        {
            ClashLog.Message("[ClashOfRim][ThingPackage] failed to restore package "
                + (reference.ThingPackageId ?? reference.GlobalKey)
                + ": "
                + ex.Message);
            missingDefName = reference.DefName;
            return false;
        }
        finally
        {
            if (initialized)
            {
                try
                {
                    Scribe.ForceStop();
                }
                catch (Exception)
                {
                    // Best-effort cleanup after a failed Scribe load.
                }
            }

            if (!string.IsNullOrWhiteSpace(tempFile))
            {
                try
                {
                    File.Delete(tempFile!);
                }
                catch (Exception)
                {
                    // Temporary load files are safe to leave behind if deletion fails.
                }
            }
        }
    }

    private static bool ShouldAttachFallbackPackage(Thing thing, ModThingReferenceDto reference)
    {
        if (thing is null
            || reference is null
            || reference.PawnPackage is not null
            || !string.IsNullOrWhiteSpace(reference.PawnPackageId)
            || reference.ThingPackage is not null
            || !string.IsNullOrWhiteSpace(reference.ThingPackageId)
            || thing is Pawn
            || thing is Corpse
            || thing is MinifiedThing
            || thing is not ThingWithComps thingWithComps)
        {
            return false;
        }

        if (thing.def?.category != ThingCategory.Item)
        {
            return false;
        }

        if (thing.def.stackLimit > 1)
        {
            return false;
        }

        List<ThingComp> comps = thingWithComps.AllComps;
        return comps is { Count: > 0 }
            && comps.Any(comp => comp is not null && !KnownCompTypeNames.Contains(comp.GetType().Name));
    }

    private static bool ReferenceDefMatches(ModThingReferenceDto reference, Thing restored)
    {
        string? expected = string.IsNullOrWhiteSpace(reference.MinifiedInnerDefName)
            ? reference.DefName
            : reference.MinifiedInnerDefName;
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected, restored.def?.defName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryCreateThingScribeXml(Thing thing)
    {
        FieldInfo? saverField = typeof(Scribe).GetField("saver", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        object? saver = saverField?.GetValue(null);
        if (saver is null)
        {
            return null;
        }

        MethodInfo? method = saver.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "DebugOutputFor", StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(typeof(Thing));
            });
        return method?.Invoke(saver, new object[] { thing }) as string;
    }

    private static string CompressToBase64(string xml)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    private static string DecompressXml(ModThingScribePayloadDto scribe)
    {
        byte[] compressed = Convert.FromBase64String(scribe.XmlGzipBase64);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        if (output.Length > MaxScribeXmlBytes)
        {
            throw new InvalidOperationException("thing package scribe xml is too large");
        }

        string xml = Encoding.UTF8.GetString(output.ToArray());
        int xmlBytes = Encoding.UTF8.GetByteCount(xml);
        if (scribe.UncompressedBytes > 0 && scribe.UncompressedBytes != xmlBytes)
        {
            throw new InvalidOperationException("thing package scribe byte count mismatch");
        }

        if (!string.IsNullOrWhiteSpace(scribe.XmlSha256)
            && !string.Equals(ComputeSha256Hex(xml), scribe.XmlSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("thing package scribe hash mismatch");
        }

        return xml;
    }

    private static string ComputeSha256Hex(string text)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
