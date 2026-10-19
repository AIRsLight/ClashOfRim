using System;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Xml;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

// Owns the native load session and its temporary file only. Arrival policy,
// extension registration, faction/identity and world relationships belong to
// PawnExchangeScribeRestorer. Native PostLoadInit itself is NOT a preview sandbox.
internal static class PawnScribeLoader
{
    internal static bool TryPrepare(ModPawnScribePayloadDto? payload, string label,
        [NotNullWhen(true)] out PawnScribeDocument? document, out string message)
    {
        var error = PawnScribeDocument.Read(payload, out document);
        message = error switch
        {
            PawnScribePayloadError.Missing => ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusMissingScribe", label.Named("LABEL")),
            PawnScribePayloadError.TooLarge => ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusScribeTooLarge", label.Named("LABEL")),
            PawnScribePayloadError.HashMismatch => ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusScribeHashMismatch", label.Named("LABEL")),
            _ => string.Empty
        };
        return error == PawnScribePayloadError.None && Available(label, out message);
    }

    internal static bool TryLoad(PawnScribeDocument document, string label, [NotNullWhen(true)] out Pawn? pawn, out string message)
    {
        pawn = null;
        message = string.Empty;
        // Never take ownership of another save/load operation, including on failure.
        if (!Available(label, out message)) return false;
        string? tempFile = null;
        bool started = false, finalized = false;
        try
        {
            string xml = document.Build(ClashOfRimCompatibilityApi.IsPawnExchangeLocalSaveOnlyLoadId,
                ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusScribeRootMissing"));
            string directory = Path.Combine(Path.GetTempPath(), "ClashOfRim", "PawnExchangeScribe");
            Directory.CreateDirectory(directory);
            tempFile = Path.Combine(directory, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".xml");
            File.WriteAllText(tempFile, xml, Encoding.UTF8);
            // InitLoading may fail after entering LoadingVars. We own its cleanup
            // from the start of that call, not only after a successful return.
            started = true;
            Scribe.loader.InitLoading(tempFile);
            Pawn? loaded = null;
            Scribe_Deep.Look(ref loaded, "pawn");
            Scribe.loader.FinalizeLoading();
            finalized = true;
            if (loaded is null)
            {
                message = ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusScribeReturnedNull", label.Named("LABEL"));
                return false;
            }
            pawn = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
            or InvalidOperationException or ArgumentException or NullReferenceException)
        {
            Log.Warning("[ClashOfRim][PawnExchange] Scribe load failed label=" + label + ": " + ex);
            message = Failure(label, ex);
            return false;
        }
        finally
        {
            try
            {
                if (started && !finalized)
                    try { Scribe.loader.ForceStop(); }
                    catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
                    { Log.Warning("[ClashOfRim] Failed to force-stop pawn Scribe loader: " + ex); }
            }
            finally
            {
                if (tempFile is not null)
                    try { File.Delete(tempFile); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static bool ValidateRuntimeState(Pawn pawn, string label, out string message)
    {
        message = string.Empty;
        try
        {
            _ = pawn.carryTracker?.GetDirectlyHeldThings();
            _ = pawn.carryTracker?.CarriedThing;
            _ = pawn.inventory?.innerContainer?.Count;
            _ = pawn.equipment?.AllEquipmentListForReading?.Count;
            _ = pawn.apparel?.WornApparel?.Count;
            return true;
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            Log.Warning("[ClashOfRim][PawnExchange] Restored pawn runtime state invalid label=" + label + " pawn=" + pawn.ThingID + ": " + ex);
            message = Failure(label, ex);
            return false;
        }
    }

    private static string Failure(string label, Exception ex) => ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusRestoreFailed",
        label.Named("LABEL"), ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));

    // Preparation checks before extension registration; loading checks again
    // after those callbacks, which may themselves enter another Scribe session.
    private static bool Available(string label, out string message)
    {
        bool available = Scribe.mode == LoadSaveMode.Inactive;
        message = available ? string.Empty
            : Failure(label, new InvalidOperationException("Scribe is already active."));
        return available;
    }
}
