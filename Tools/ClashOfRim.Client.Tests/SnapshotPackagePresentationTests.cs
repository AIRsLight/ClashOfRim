using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using HarmonyLib;

internal static class SnapshotPackagePresentationTests
{
    private static readonly List<string> Translations = new();
    private static string language = "en";
    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.SnapshotPackagePresentation");
        harmony.Patch(typeof(ClashOfRimText).GetMethod("Key", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(string) }, null),
            prefix: new HarmonyMethod(typeof(SnapshotPackagePresentationTests), nameof(Translate)));
        try
        {
            var cases = new[]
            {
                (Data: SnapshotPackageModuleTests.Save(), Owner: "", Error: SnapshotPackageError.MissingIdentity),
                (Data: Array.Empty<byte>(), Owner: "owner", Error: SnapshotPackageError.MissingSavePayload),
                (Data: SnapshotPackageModuleTests.Save(""), Owner: "owner", Error: SnapshotPackageError.MissingRimWorldVersion)
            };
            foreach (var item in cases)
            {
                Translations.Clear();
                var pure = ClientSnapshotPackageBuilder.FromSaveBytes(item.Data, "test", item.Owner, "colony", SnapshotPackageModuleTests.Timestamp);
                Require(pure.ErrorKind == item.Error && pure.Message is null && Translations.Count == 0, "Data-only packaging invoked presentation.");
                var preparation = new ClientSnapshotPreparation(new Game(item.Data));
                Require(!preparation.TryBuild(item.Owner, "colony", out _, out var failure)
                    && failure?.PackageError == item.Error && failure.Message is null && Translations.Count == 0,
                    "Preparation lost the structured failure or localized it early.");
                foreach (string displayLanguage in new[] { "en", "fr" })
                {
                    language = displayLanguage;
                    string expected = language + ":ClashOfRim.SnapshotPackage." + item.Error;
                    var legacy = ModSnapshotPackageBuilder.FromSaveBytes(item.Data, "test", item.Owner, "colony", SnapshotPackageModuleTests.Timestamp);
                    Require(!legacy.Success && legacy.ErrorCode == item.Error.ToString() && legacy.Message == expected,
                        "The public package API changed its error code or localized text.");
                    var upload = ClientSnapshotUploadPresentation.ToPublicResult(new(null, null, failure));
                    Require(!upload.Success && upload.ErrorCode == item.Error.ToString() && upload.Message == expected,
                        "Upload presentation replaced the package-specific failure with a generic message.");
                    Require(!new ClientSnapshotPreparationAdapter(new Game(item.Data)).TryBuild(item.Owner, "colony", out _, out string reason)
                        && reason == item.Error + " " + expected, "Legacy domain preparation changed its failure format.");
                    Require(pure.Message is null && pure.ErrorKind == item.Error, "Presentation mutated a reusable data result.");
                }
            }

            Translations.Clear();
            var invalid = ClientSnapshotPackageBuilder.FromSaveBytes(Encoding.UTF8.GetBytes("<savegame>"), "invalid", "owner", "colony", SnapshotPackageModuleTests.Timestamp);
            Require(ReferenceEquals(invalid, ClientSnapshotPackagePresentation.ToPublicResult(invalid)) && Translations.Count == 0,
                "Exception diagnostics were translated or replaced.");
            var input = SnapshotPackageModuleTests.Save();
            Require(ReferenceEquals(input, SnapshotSaveSanitizer.RemovePendingAchievementQueue(input)), "The public cleanup facade rewrote a save without pending achievements.");
            var success = ModSnapshotPackageBuilder.FromSaveBytes(input, "source", "owner", "colony", SnapshotPackageModuleTests.Timestamp);
            Require(success.Success && success.ErrorCode is null && success.Message is null && success.SourcePath == "source"
                && ReferenceEquals(success.OriginalPayload, input) && Translations.Count == 0, "The public success path invoked display or replaced its payload.");

            var capture = new SnapshotUploadFailure("SaveToMemoryFailed", "engine detail", SnapshotUploadMessage.SaveFailed);
            var adapter = new ClientSnapshotPreparationAdapter(new Game(input) { CaptureFailure = capture });
            Require(!adapter.TryBuild("owner", "colony", out var build, out string captureReason)
                && build.ErrorCode == capture.Code && captureReason == "engine detail" && Translations.Count == 0,
                "Capture failure was reclassified as a package failure.");
        }
        finally { harmony.UnpatchAll(harmony.Id); Translations.Clear(); }
    }

    private static bool Translate(string key, ref string __result)
    { Translations.Add(key); __result = language + ":" + key; return false; }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class Game : IClientSnapshotGame, IClientSnapshotGameSession
    {
        private readonly byte[] data;
        internal Game(byte[] data) => this.data = data;
        internal SnapshotUploadFailure? CaptureFailure;
        public IClientSnapshotGameSession BindCurrentGame() => this;
        public bool HasGame => true;
        public string RuntimeSnapshotId => "base";
        public SnapshotSaveCapture Capture(bool removeRaidBattleSessions) => new(data, null, Array.Empty<ModSnapshotAchievementCandidateDto>(), CaptureFailure);
        public void Enqueue(Action action, Action onDiscard) => throw new Exception("Unexpected dispatch during synchronous preparation.");
        public void Publish(string id, string? token, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements) => throw new Exception("Unexpected publication during preparation.");
    }
}
