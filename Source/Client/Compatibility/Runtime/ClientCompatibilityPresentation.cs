using AIRsLight.ClashOfRim.Modules.Compatibility;
using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Modules.Transport;
using Verse;

namespace AIRsLight.ClashOfRim;

// The factory constructs the existing compatibility UI. Selection, publication and
// continuation ownership are independent of the legacy Mod used by that window.
internal sealed class ClientCompatibilityPresentation
{
    private readonly Func<ModLoginResponseDto, string?, Action?, Action?, Window> createWindow;
    internal ClientCompatibilityPresentation(Func<ModLoginResponseDto, string?, Action?, Action?, Window> createWindow) => this.createWindow = createWindow;
    internal void Show(ModLoginResponseDto? response, bool languageAccepted, string? language = null,
        Action? accept = null, Action? cancel = null, Func<bool>? current = null)
    {
        if (!ClientCompatibilityWarning.ShouldShow(response, languageAccepted)) { cancel?.Invoke(); return; }
        var owned = ClientProtocolJson.Copy(response!);
        if (owned.CompatibilityIssues is { Count: > 0 })
            ClashLog.Message("[ClashOfRim][Compatibility] mismatch issues:\n" + string.Join("\n", owned.CompatibilityIssues.Select(issue =>
                "  - severity=" + issue.Severity + ", code=" + issue.Code + ", subject=" + issue.Subject + ", message=" + issue.Message)));
        ClientMainThreadDispatcher.Enqueue(() =>
        {
            if ((current?.Invoke() ?? true) && Find.WindowStack.WindowOfType<CompatibilityMismatchWindow>() is null)
                Find.WindowStack.Add(createWindow(owned, language, accept, cancel));
            else cancel?.Invoke();
        }, cancel);
    }
}
