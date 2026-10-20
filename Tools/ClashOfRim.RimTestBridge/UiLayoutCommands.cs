using RimTest.Protocol;
using RimTest.Runtime;
using UnityEngine;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed class UiLayoutCommands
{
    internal UiLayoutCommands(CommandRouter router, BridgeActionPolicy policy, Func<string, Window> create)
    {
        router.Register("clash.ui.configure", "FIXTURE: set the isolated game's resolution and UI scale for real repaint layout tests.", "fixture", a =>
        {
            policy.RequireFixture(a); UiLayoutObservation.Install(); Close();
            int width = a.Int("width"), height = a.Int("height");
            float scale = a.GetProperty("scale").GetSingle();
            if (width < 1024 || height < 720 || width > 2560 || height > 1440 || scale < 1 || scale > 1.5f)
                throw new CommandException("invalid_arguments", "Expected supported test resolution and scale.");
            Prefs.UIScale = scale; Screen.SetResolution(width, height, false); Prefs.DevMode = true;
            return new { configured = true };
        });
        router.Register("clash.ui.open", "FIXTURE: open an original mod window or select its page for layout observation; never submit business actions.", "fixture", a =>
        {
            policy.RequireFixture(a); Close();
            string target = a.String("target");
            if (target == "close") return new { frame = UiLayoutObservation.Frame };
            var window = create(target);
            Find.WindowStack.Add(window); UiLayoutObservation.Target = window;
            return new { frame = UiLayoutObservation.Frame };
        });
        router.Register("clash.ui.read", "Observe the selected window's latest real repaint, native font measurements and bounds.", "observe", _ => new
        {
            frame = UiLayoutObservation.Frame, language = LanguageDatabase.activeLanguage.folderName,
            screenWidth = UI.screenWidth, screenHeight = UI.screenHeight, pixelWidth = Screen.width, pixelHeight = Screen.height,
            scale = Prefs.UIScale, window = UiLayoutObservation.Target?.GetType().FullName,
            bounds = UiLayoutObservation.Target is { } w ? UiLayoutObservation.Bounds(w.windowRect) : null,
            labels = UiLayoutObservation.Labels
        });
        router.Register("clash.ui.capture", "FIXTURE: capture the game's rendered frame into this isolated profile's UiEvidence directory.", "fixture", a =>
        {
            policy.RequireFixture(a);
            string name = a.String("name");
            if (name.Length > 100 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                throw new CommandException("invalid_arguments", "Expected simple evidence name.");
            string folder = Path.Combine(GenFilePaths.SaveDataFolderPath, "UiEvidence"); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name + ".png"); ScreenCapture.CaptureScreenshot(path);
            return new { path };
        });
    }
    private static void Close()
    {
        UiLayoutObservation.Target?.Close(doCloseSound: false); UiLayoutObservation.Target = null;
    }
}
