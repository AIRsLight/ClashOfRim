using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using RimTest.Protocol;
using RimTest.Runtime;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed class CompatibilityCommands
{
    internal CompatibilityCommands(CommandRouter router, BridgeActionPolicy policy)
    {
        router.Register("clash.compatibility", "Observe the original compatibility decision; only accepted language warnings offer continuation.", "observe", _ =>
        {
            var window = Find.WindowStack.WindowOfType<CompatibilityMismatchWindow>();
            var model = window is null ? null : BridgeReflection.Field<ClientCompatibilityMismatch>(window, "model");
            return new { open = window is not null, canContinue = window?.CanContinue == true,
                canApplyManifest = model?.CanApplyManifest == true, canApplyConfig = model?.CanApplyConfig == true,
                manifestDifferences = model?.Report?.ManifestEntries.Count ?? 0,
                fileDifferences = model?.Report?.HashEntries.Count ?? 0, configDifferences = model?.Report?.ConfigEntries.Count ?? 0,
                details = model?.Report?.HashEntries.Select(e => new { e.Title, e.Subject, e.Detail }).ToArray() };
        });
        router.Register("clash.compatibility.close", "Close the original compatibility window and cancel its pending entry flow.", "semantic", _ =>
        {
            policy.RequireLoopback();
            var window = Find.WindowStack.WindowOfType<CompatibilityMismatchWindow>();
            if (window is null) throw new CommandException("invalid_context", "No compatibility window.");
            window.Close(); return new { invoked = true };
        });
        router.Register("clash.compatibility.continue", "Choose the original Join Anyway button when the product permits a language-only warning.", "semantic", _ =>
        {
            policy.RequireLoopback();
            var window = Find.WindowStack.WindowOfType<CompatibilityMismatchWindow>();
            if (window?.TryContinue() != true) throw new CommandException("disabled", "Compatibility cannot be bypassed.");
            return new { invoked = true };
        });
    }
}
