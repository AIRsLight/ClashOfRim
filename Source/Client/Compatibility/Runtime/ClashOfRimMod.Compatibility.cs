using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void CaptureServerCompatibilityManifest(string? manifestJson) => compatibilityBaseline.Apply(manifestJson);

    internal Task StartOverrideCompatibilityBaselineFromCurrentClient(Action<string, float>? progress, Action<string, bool> completion)
    {
        CloseServerEntryProgressWindowNow();
        return compatibilityOverride.StartAsync(progress, completion);
    }

    private void ShowCompatibilityMismatchWindow(
        ModLoginResponseDto? response,
        Action? continueAnyway = null,
        Action? cancelContinuation = null,
        string? authoritativeServerGameLanguage = null)
        => compatibilityPresentation.Show(response, worldSetup.LanguageAccepted, authoritativeServerGameLanguage, continueAnyway, cancelContinuation);

    private void ShowCompatibilityMismatchWindow(
        ModPrepareWorldSessionResponseDto? response,
        Action? continueAnyway = null,
        Action? cancelContinuation = null)
        => compatibilityPresentation.Show(response is null ? null : ClientCompatibilityWarning.FromPreparation(response),
            worldSetup.LanguageAccepted, response?.WorldConfiguration?.GameLanguage, continueAnyway, cancelContinuation);
}
