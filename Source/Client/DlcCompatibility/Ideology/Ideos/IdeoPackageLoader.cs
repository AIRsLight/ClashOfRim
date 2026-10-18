using System;
using System.IO;
using System.Text;
using AIRsLight.ClashOfRim.Modules.Compatibility.Ideology;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Ideology.Ideos;

// Synchronous engine adapter: callers retain their game/setup execution context.
// Loading resolves native references; initialization and catalog ownership belong
// to the materializer, so a local normalization roundtrip uses this same loader.
internal sealed class IdeoPackageLoader
{
    private readonly Action<string> warning;

    internal IdeoPackageLoader(Action<string> warning) => this.warning = warning;

    internal Ideo? TryLoad(string? xml, string? expectedSha256, string globalKey)
    {
        if (string.IsNullOrWhiteSpace(xml) || Scribe.mode != LoadSaveMode.Inactive)
            return null;

        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !string.Equals(IdeoPackageHash.Compute(xml), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            warning($"[ClashOfRim] Skipped remote ideo package with mismatched hash {globalKey}.");
            return null;
        }

        string? path = null;
        bool loadStarted = false;
        try
        {
            string directory = Path.Combine(Path.GetTempPath(), "ClashOfRim", "RemoteIdeos");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "remote-ideo-" + Guid.NewGuid().ToString("N") + ".ideo");
            File.WriteAllText(path, xml, Encoding.UTF8);
            loadStarted = true;
            return GameDataSaveLoader.TryLoadIdeo(path, out Ideo loaded) ? loaded : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            warning($"[ClashOfRim] Failed to load remote ideo package {globalKey}: {ex}");
            return null;
        }
        finally
        {
            try
            {
                // Never stop another operation's Scribe session. Only clean up
                // after entering the native loader from an inactive session.
                if (loadStarted && Scribe.mode != LoadSaveMode.Inactive)
                    Scribe.ForceStop();
            }
            finally
            {
                if (path is not null) TryDelete(path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
