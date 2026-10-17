using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using HarmonyLib;
using Verse;

internal static class EventConfirmationPresentationTests
{
    private static Dictionary<string, string> translations = new();
    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.EventConfirmationPresentation");
        harmony.Patch(AccessTools.Method(typeof(ClashOfRimText), "Key", new[] { typeof(string) }),
            prefix: new HarmonyMethod(typeof(EventConfirmationPresentationTests), nameof(Translate)));
        harmony.Patch(AccessTools.Method(typeof(ClashOfRimText), "Key", new[] { typeof(string), typeof(NamedArgument[]) }),
            prefix: new HarmonyMethod(typeof(EventConfirmationPresentationTests), nameof(TranslateArguments)));
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Languages"))) root = root.Parent;
            if (root is null) throw new Exception("Cannot locate shipped translations.");
            var presentation = new ClientEventConfirmationPresentation(static (_, _) => { });
            foreach (var culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "Languages"), "ClashOfRim.xml", SearchOption.AllDirectories))
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                translations = XDocument.Load(file).Root!.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
                foreach (var purpose in Enum.GetValues<ClientEventConfirmationPurpose>())
                {
                    var notice = new ClientEventConfirmationNotice(purpose, ClientEventConfirmationStage.Uploading,
                        "event-é", 7, "snapshot", Rejected: 2, Pending: 4);
                    RequireResolved(presentation.Format(notice));
                    foreach (var failure in new[]
                    {
                        new ClientEventPreparationFailure(ClientEventPreparationFailureKind.Busy),
                        new ClientEventPreparationFailure(ClientEventPreparationFailureKind.Admission, Admission: LocalMutationAdmission.NotConfigured),
                        new ClientEventPreparationFailure(ClientEventPreparationFailureKind.Admission, Admission: LocalMutationAdmission.SnapshotMissing),
                        new ClientEventPreparationFailure(ClientEventPreparationFailureKind.Blocked,
                            Blocked: new(LocalMutationKind.Bank, "bank-pending", default))
                    }) RequireResolved(presentation.Format(notice with { Stage = ClientEventConfirmationStage.PreparationFailed, PreparationFailure = failure }));
                    foreach (var kind in Enum.GetValues<ClientEventConfirmationFailureKind>())
                    {
                        string text = presentation.Format(notice with { Stage = ClientEventConfirmationStage.Failed,
                            Problem = new(kind, "error-code", "error-message", "validation-detail", new InvalidOperationException("exception"), false) });
                        RequireResolved(text);
                        if (!text.Contains("error-message") || !text.Contains(kind == ClientEventConfirmationFailureKind.Exception ? "InvalidOperationException" : "error-code"))
                            throw new Exception("Confirmation failure lost semantic parameters: " + purpose);
                        if (kind == ClientEventConfirmationFailureKind.Rejected && purpose != ClientEventConfirmationPurpose.AutomaticEvents
                            && !text.Contains("validation-detail")) throw new Exception("Single rejection lost server validation.");
                    }
                    // Raid acceptance is guarded and presented by the raid domain. It never builds a new package here.
                    if (purpose != ClientEventConfirmationPurpose.RaidSettlement)
                    {
                        RequireResolved(presentation.Format(notice with { Stage = ClientEventConfirmationStage.Accepted }));
                        string build = presentation.Format(notice with { Stage = ClientEventConfirmationStage.PreparationFailed,
                            PreparationFailure = new(ClientEventPreparationFailureKind.Build,
                                SnapshotFailure: new("capture", "save-reason", SnapshotUploadMessage.SaveFailed)) });
                        if (!build.Contains("save-reason")) throw new Exception("Build failure lost reason.");
                        RequireResolved(build);
                    }
                    if (purpose is not (ClientEventConfirmationPurpose.RaidSettlement or ClientEventConfirmationPurpose.AutomaticEvents))
                        RequireResolved(presentation.Format(notice with { Stage = ClientEventConfirmationStage.Empty }));
                }
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; harmony.UnpatchAll(harmony.Id); translations.Clear(); }
    }

    private static bool Translate(string key, ref string __result)
    {
        if (!translations.TryGetValue(key, out var value)) throw new Exception("Unknown confirmation translation: " + key);
        __result = value;
        return false;
    }
    private static bool TranslateArguments(string key, NamedArgument[] args, ref string __result)
    {
        Translate(key, ref __result);
        foreach (var arg in args) __result = __result.Replace("{" + arg.label + "}", Convert.ToString(arg.arg, CultureInfo.CurrentCulture));
        return false;
    }
    private static void RequireResolved(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('{') || text.Contains('}'))
            throw new Exception("Unresolved confirmation text: " + text);
    }
}
