using AIRsLight.ClashOfRim.Modules.Compatibility;
using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.CompatibilityClient;
using Verse;

namespace AIRsLight.ClashOfRim;

// Capture the decision owner before queueing; build its engine-backed report only
// on the UI thread, then give the window the already-owned module.
internal sealed class ClientCompatibilityPresentation
{
    private readonly Func<ModLoginResponseDto, string?, Action?, Action?, Func<bool>?, ClientCompatibilityMismatch> create;
    internal ClientCompatibilityPresentation(Func<ModLoginResponseDto, string?, Action?, Action?, Func<bool>?, ClientCompatibilityMismatch> create) => this.create = create;
    internal void Show(ModLoginResponseDto? response, bool languageAccepted, string? language = null,
        Action? accept = null, Action? cancel = null, Func<bool>? current = null)
    {
        if (!ClientCompatibilityWarning.ShouldShow(response, languageAccepted)) { cancel?.Invoke(); return; }
        var model = create(response!, language, accept, cancel, current);
        string issues = model.IssueSummary;
        if (issues.Length > 0) ClashLog.Message("[ClashOfRim][Compatibility] mismatch issues:\n" + issues);
        ClientMainThreadDispatcher.Enqueue(() =>
        {
            var previous = Find.WindowStack.WindowOfType<CompatibilityMismatchWindow>();
            if (previous is not null && !previous.IsCurrent) previous.Close();
            if (Find.WindowStack.WindowOfType<CompatibilityMismatchWindow>() is null && model.Open())
                Find.WindowStack.Add(new CompatibilityMismatchWindow(model));
            else model.Close();
        }, model.Close);
    }
}
