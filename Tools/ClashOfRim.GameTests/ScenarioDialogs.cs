using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

// Only original offered buttons are selected. Localized Steam notices and
// language-only compatibility warnings retain their distinct permissions.
internal sealed class ScenarioDialogs(TestRun run, bool allowLanguageWarning)
{
    internal async Task Confirm(string client)
    {
        var dialogs = Items(await run.CallAsync(client, "dialogs.messages"));
        if (dialogs.Length == 0) return;
        var labels = await run.CallAsync(client, "clash.labels");
        foreach (var dialog in dialogs)
        {
            var buttons = dialog.GetProperty("buttons").EnumerateArray().ToArray();
            // Isolated offline profiles may lack Steam; the game explicitly offers continuing.
            // Match the current game's translated text and original label, never a fixed position/language.
            var choice = dialog.String("text") == labels.String("steamClientMissing")
                ? buttons.Single(b => b.String("label") == labels.String("ignore"))
                : buttons.Single(b => b.String("id") == "A");
            Check(choice.String("label") != labels.String("quit"), "Unexpected quit dialog: " + dialog.GetRawText());
            await run.CallAsync(client, "dialogs.choose", new { dialog = H(dialog), button = choice.String("id") });
        }
    }
    internal async Task<bool> AcceptLanguageWarning(string client)
    {
        var warning = await run.CallAsync(client, "clash.compatibility");
        if (!warning.Bool("open")) return false;
        Check(allowLanguageWarning && warning.Bool("canContinue"), "Unexpected compatibility mismatch; do not bypass it.");
        await run.CallAsync(client, "clash.compatibility.continue");
        return true;
    }
}
