using AIRsLight.ClashOfRim.CompatibilityClient;
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
            return new { open = window is not null, canContinue = window?.CanContinue == true };
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
