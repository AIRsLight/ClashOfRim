using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using static AIRsLight.ClashOfRim.Modules.Compatibility.ClientCompatibilityReport;

internal static class ClientCompatibilityMismatchTests
{
    internal static void Run() => RunAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Report(); UpdatedFiles(); Fallback(); Malformed(); Continue(); Repairs(); Queued(); Exceptions();
                foreach (string change in new[] { "close", "user", "server", "colony", "auth", "login", "game", "generation", "password", "steam", "unavailable", "caller" })
                    foreach (bool opened in new[] { false, true }) Stale(change, opened);
                foreach (string phase in new[] { "capture", "text", "close", "repair", "order" }) Reentry(phase);
                foreach (string fault in new[] { "network", "rejected", "exception" }) await Override(fault);
                await Override(null); await OrderChange();
            }
            Console.WriteLine("PASS: compatibility mismatch (four cultures; immutable reports, owned recovery, continuation and shared override)");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static CompatibilityManifest Manifest() => new()
    {
        ManifestId = "manifest-id-123456789", ProtocolVersion = ClashOfRimVersion.ProtocolVersion, RimWorldVersion = "1.7", GameLanguage = "English",
        Mods = new[] { new ModManifestEntry { PackageId = "mod.i", Name = "文化 İ", WorkshopId = " 12345 ", Files = new[]
        { new ControlledFileEntry { RelativePath = "Defs/Test.xml", Sha256 = "ABCDEF", Size = 10, LastWriteUtcUnix = 100 } },
            Configs = new[] { new ModConfigDigest { FileName = "Settings", Sha256 = "AAA", CanonicalXml = "<settings />" } } } },
        DefSummaries = new[] { new DefSummary { Name = "ThingDef", Count = 1, Hash = 7 } }
    };
    private static string Json(CompatibilityManifest manifest) => CompatibilityManifestJsonWriter.Write(manifest);
    private static ModCompatibilityIssueDto Issue(string code, string severity = "Error", string subject = "server-only")
        => new() { Code = code, Severity = severity, Subject = subject, Message = "  authoritative 文化  " };
    private static ModLoginResponseDto Response(CompatibilityManifest? manifest = null) => new()
    { Result = new() { Accepted = false }, CanOverrideCompatibilityBaseline = true, ServerCompatibilityManifestJson = Json(manifest ?? Manifest()) };

    private static void Report()
    {
        var f = new Fixture(); var original = Manifest(); var originalMod = original.Mods[0];
        var same = new ClientCompatibilityReport(Response(original), Json(original), " ChineseSimplified ", f);
        Check(same.ManifestEntries.Count == 0 && same.HashEntries.Count == 0 && same.ConfigEntries.Count == 0
            && same.ServerLanguage == "ChineseSimplified" && same.LocalLanguage == "English" && !same.ProtocolMismatch, "Matching report or authoritative language changed.");
        var changed = original with { ConfigSha256 = "server", Mods = new[] { originalMod with
        { Files = new[] { originalMod.Files[0] with { Size = 11, Sha256 = "FFF", LastWriteUtcUnix = 200 },
            new ControlledFileEntry { RelativePath = "Defs/Missing.xml" } }, Configs = new[] { originalMod.Configs[0] with { Sha256 = "BBB" } } },
            new ModManifestEntry { LoadOrder = 1, PackageId = "missing", Name = "Missing mod", WorkshopId = " 6789 " } },
            DefSummaries = new[] { original.DefSummaries[0] with { Count = 2 }, new DefSummary { Name = "OtherDef" } } };
        var local = original with { Mods = new[] { originalMod with { PackageId = " MOD.I ", Files = new[]
        { originalMod.Files[0] with { RelativePath = "defs\\test.XML", Sha256 = "abcdef" }, new ControlledFileEntry { RelativePath = "extra" } },
            Configs = originalMod.Configs.Concat(new[] { new ModConfigDigest { FileName = "local-extra", Sha256 = "untouched" } }).ToArray() },
            new ModManifestEntry { LoadOrder = 1, PackageId = "local-only" } },
            DefSummaries = original.DefSummaries.Concat(new[] { new DefSummary { Name = "ExtraDef" } }).ToArray() };
        var response = Response(changed);
        var report = new ClientCompatibilityReport(response, Json(local), null, f);
        Check(report.ManifestEntries.Count == 3 && report.HashEntries.Count == 7 && report.ConfigEntries.Count == 2,
            $"Report lost mod/file/def/config differences: {report.ManifestEntries.Count}/{report.HashEntries.Count}/{report.ConfigEntries.Count}.");
        Check(report.HashEntries.Any(e => e.Detail.Contains("FileTimeHintClientMayNeedUpdate", StringComparison.Ordinal))
            && report.ConfigEntries.All(e => !e.Detail.Contains("local-extra", StringComparison.Ordinal)), "Version hint or local config passthrough changed.");
        Check(report.ServerMods[1].Status == UiDiffStatus.Missing && report.LocalMods[1].Status == UiDiffStatus.Added
            && report.ServerMods[0].Status == UiDiffStatus.Match, "Column status lost normalized mod identity.");
        Check(report.WorkshopId(report.ManifestEntries.First(e => e.Status == UiDiffStatus.Missing)) == "6789"
            && report.WorkshopId(report.ManifestEntries.First(e => e.Status == UiDiffStatus.Modified)) is null, "Workshop target changed.");
        response.ServerCompatibilityManifestJson = "{}"; response.CanOverrideCompatibilityBaseline = false;
        Check(report.ManifestJson == Json(changed) && report.CanOverride, "Report retained mutable response state.");
        var fileOnly = new ClientCompatibilityReport(Response(original with { Mods = new[] { originalMod with
        { Files = new[] { originalMod.Files[0] with { Sha256 = "different" } } } } }), Json(original), null, f);
        Check(fileOnly.RequiresManualUpdate && !fileOnly.CanApplyManifest && !fileOnly.CanApplyConfig, "File-only mismatch offered an ineffective restart repair.");
        var protocol = new ClientCompatibilityReport(Response(original with { ProtocolVersion = "other" }), Json(original), null, f);
        Check(protocol.ProtocolMismatch && protocol.ManifestEntries.Count == 0, "Protocol mismatch became a mod list entry.");
    }
    private static void UpdatedFiles()
    {
        var f = new Fixture(); var original = Manifest();
        foreach (string difference in new[] { "version", "schema", "files", "version-and-files" })
        foreach (bool administrator in new[] { false, true })
        {
            var changed = original with
            {
                RimWorldVersion = difference.Contains("version") ? "1.7.updated" : original.RimWorldVersion,
                SchemaVersion = difference == "schema" ? original.SchemaVersion + 1 : original.SchemaVersion,
                Mods = difference.Contains("files") ? new[] { original.Mods[0] with
                { Files = new[] { original.Mods[0].Files[0] with { Sha256 = "updated" } } } } : original.Mods
            };
            var response = Response(original); response.CanOverrideCompatibilityBaseline = administrator;
            var report = new ClientCompatibilityReport(response, Json(changed), null, f);
            Check(report.RequiresManualUpdate && !report.CanApplyManifest && !report.CanApplyConfig && report.CanOverride == administrator,
                "File/version update offered an ineffective mod-list repair or lost administrator recovery: " + difference);
            changed = changed with { ConfigSha256 = "changed", DlcIds = new[] { "Ideology" } };
            report = new ClientCompatibilityReport(response, Json(changed), null, f);
            Check(report.RequiresManualUpdate && report.CanApplyManifest && report.CanApplyConfig && report.CanOverride == administrator,
                "Mixed differences hid an applicable repair or the remaining manual update: " + difference);
        }
        foreach (string code in new[] { "RimWorldVersionMismatch", "SchemaVersionMismatch", "ProtocolVersionMismatch", "FutureCode" })
        {
            var response = Response(); response.CompatibilityIssues = new() { Issue(code) };
            var report = new ClientCompatibilityReport(response, Json(original), null, f);
            Check(!report.CanApplyManifest, "Fallback issue was mistaken for an applicable mod list: " + code);
            Check(report.RequiresManualUpdate == (code != "FutureCode"), "Version/protocol fallback lost its manual update explanation.");
        }
        foreach (string code in new[] { "DlcListMismatch", "ModListMismatch", "ModOrderMismatch", "MissingMod", "UnexpectedMod" })
        {
            var response = Response(); response.CompatibilityIssues = new() { Issue(code) };
            var report = new ClientCompatibilityReport(response, Json(original), null, f);
            Check(report.CanApplyManifest, "Actionable mod selection no longer offers repair: " + code);
            response.ServerCompatibilityManifestJson = null;
            Check(!new ClientCompatibilityReport(response, Json(original), null, f).CanApplyManifest, "Repair offered without a server manifest.");
        }
    }
    private static void Fallback()
    {
        var f = new Fixture();
        foreach (CompatibilityIssueCode code in Enum.GetValues(typeof(CompatibilityIssueCode)))
        {
            var response = Response(); response.ServerCompatibilityManifestJson = null;
            response.CompatibilityIssues = new() { Issue(code.ToString()) };
            var report = new ClientCompatibilityReport(response, null, null, f);
            var category = CompatibilityIssueClassifier.CategoryFor(code);
            if (code == CompatibilityIssueCode.GameLanguageMismatch) category = CompatibilityIssueCategory.Overview;
            Check(report.ManifestEntries.Count == (category == CompatibilityIssueCategory.Manifest ? 1 : 0)
                && report.HashEntries.Count == (category == CompatibilityIssueCategory.Hash ? 1 : 0)
                && report.ConfigEntries.Count == (category == CompatibilityIssueCategory.Config ? 1 : 0), "Issue category changed: " + code);
        }
        foreach (bool known in new[] { false, true })
        {
            var response = Response(); response.CompatibilityIssues = new()
            { Issue("ConfigFileWarning", "Warning"), Issue("FileHashMismatch"), Issue("MissingMod"), Issue("FutureCode") };
            if (!known) response.ServerCompatibilityManifestJson = null;
            var report = new ClientCompatibilityReport(response, known ? Json(Manifest()) : null, null, f);
            Check(report.ConfigEntries.Count == 1 && report.HashEntries.Count == 1 && report.ManifestEntries.Count == (known ? 1 : 2)
                && report.ConfigEntries[0].Detail.Contains("authoritative 文化", StringComparison.Ordinal), "Authoritative fallback was lost or duplicated by subject.");
        }
    }
    private static void Malformed()
    {
        var f = new Fixture();
        foreach (string json in new[] { "not json", "null", "{\"mods\":[null]}",
            "{\"mods\":[{\"packageId\":\"I\"},{\"packageId\":\"i\"}]}",
            "{\"mods\":[{\"files\":[{\"relativePath\":\"a/b\"},{\"relativePath\":\"A\\\\B\"}]}]}",
            "{\"mods\":[{\"configs\":[{\"fileName\":\"x\"},{\"fileName\":\"X\"}]}]}",
            "{\"defSummaries\":[{\"name\":\"x\"},{\"name\":\"X\"}]}" })
        {
            int before = f.Diagnostics;
            var response = Response(); response.ServerCompatibilityManifestJson = json; response.CompatibilityIssues = new() { Issue("ConfigFileMissing") };
            var report = new ClientCompatibilityReport(response, Json(Manifest()), null, f);
            Check(f.Diagnostics == before + 1 && report.ConfigEntries.Count == 1 && report.ServerMods.Count == 0
                && !report.CanApplyConfig, "Malformed manifest crashed or authorized config repair: " + json);
        }
        var sparse = new ClientCompatibilityReport(new() { Result = new(), ServerCompatibilityManifestJson = "{}" }, "{}", null, f);
        Check(sparse.ServerMods.Count == 0 && sparse.HashEntries.Count == 0, "Sparse manifest collections were not normalized.");
    }
    private static void Continue()
    {
        var f = new Fixture(); f.Language(); var model = f.Create(); Check(model.Open() && model.CanContinue, "Language warning cannot continue.");
        Check(!model.CanOverride && !model.CanApplyManifest && model.PrepareOverride() is null, "Language-only continuation offered baseline overwrite.");
        Check(model.Continue(() => { f.Closed++; model.Close(); }) && f.Accepted == 1 && f.Cancelled == 0 && f.Closed == 1,
            "Continue ran cancel or lost its action.");
        model.Close(); Check(!model.Continue(() => f.Closed++) && f.Accepted == 1 && f.Cancelled == 0, "Continuation consumed twice.");
        foreach (string block in new[] { "error", "rejected", "additional", "absent" })
        {
            f = new(); f.Language();
            if (block == "error") f.Response.CompatibilityIssues[0].Severity = "Error";
            if (block == "rejected") f.Response.Result!.Accepted = false;
            if (block == "additional") f.Response.CompatibilityIssues.Add(Issue("MissingMod"));
            model = f.Create(accept: block != "absent"); Check(model.Open() && !model.CanContinue && !model.Continue(() => { }), "Disallowed warning continued: " + block);
            model.Close(); model.Close(); Check(f.Cancelled == 1 && f.Accepted == 0, "Closing did not release continuation exactly once.");
        }
    }
    private static void Repairs()
    {
        foreach (ClientCompatibilityRepair kind in Enum.GetValues(typeof(ClientCompatibilityRepair)))
        {
            var f = new Fixture(); var model = f.Create(); Check(model.Open(), "Repair report did not open.");
            f.Response.ServerCompatibilityManifestJson = "replacement";
            Check(model.Apply(kind) && f.Repairs.Single() == (kind, f.OriginalServer) && model.Status == "applied", "Repair changed kind or borrowed a later manifest.");
            Check(!model.Apply((ClientCompatibilityRepair)999), "Unknown repair fell through to local overwrite.");
            model.Close(); Check(!model.Apply(kind) && f.Repairs.Count == 1, "Closed window repaired local configuration.");
        }
    }
    private static void Queued()
    {
        var f = new Fixture(); var model = f.Create();
        f.Response.CanOverrideCompatibilityBaseline = false; f.Response.ServerCompatibilityManifestJson = "{}";
        f.Response.Result!.Accepted = true; f.Response.CompatibilityIssues.Add(Issue("GameLanguageMismatch", "Warning"));
        Check(model.Open() && model.CanOverride && !model.CanContinue && model.Report!.ManifestJson == f.OriginalServer,
            "Queued report borrowed a mutated response.");
        var report = model.Report;
        Check(model.Open() && ReferenceEquals(model.Report, report) && f.Recovery.Trace.Count(x => x == "manifest") == 1,
            "Opening an existing report recaptured engine data.");
        foreach (string change in new[] { "game", "password", "login" })
        {
            f = new(); model = f.Create(); f.OnCurrent = () => f.Recovery.Change(change);
            Check(!model.Open() && f.Cancelled == 1, "Caller predicate changed ownership after its check: " + change);
        }
    }
    private static void Exceptions()
    {
        foreach (string phase in new[] { "capture", "text", "repair" })
        {
            var f = new Fixture(); var model = f.Create();
            if (phase == "repair") model.Open();
            Action fail = () => throw new InvalidOperationException("injected-report");
            if (phase == "capture") f.Recovery.Hook = at => { if (at == "manifest") fail(); };
            else if (phase == "text") f.OnText = fail;
            else f.OnApply = fail;
            try
            {
                if (phase == "repair") model.Apply(ClientCompatibilityRepair.Manifest); else model.Open();
                throw new Exception("Expected report failure was lost.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "injected-report") { }
            f.Recovery.Hook = null; f.OnText = f.OnApply = null;
            if (phase == "repair")
                Check(model.Apply(ClientCompatibilityRepair.Manifest) && f.Repairs.Count == 2, "Repair exception retained admission.");
            else Check(!model.IsCurrent && f.Cancelled == 1 && model.Report is null, "Report failure retained its continuation.");
        }
    }
    private static void Stale(string change, bool opened)
    {
        var f = new Fixture(); var model = f.Create(); if (opened) Check(model.Open(), "Fixture report failed.");
        var intent = opened ? model.PrepareOverride() : null;
        if (change == "close") model.Close(); else if (change == "caller") f.Caller = false; else f.Recovery.Change(change);
        int captures = f.Recovery.Trace.Count(x => x == "manifest");
        Check(!model.Open() && !model.IsCurrent && !model.CanOverride && !model.Continue(() => f.Closed++)
            && !model.Apply(ClientCompatibilityRepair.Manifest) && model.PrepareOverride() is null, "Old mismatch crossed " + change);
        Check(intent is null || !intent.CanStart && intent.StartAsync(null, (_, _) => { }) is null, "Old override crossed " + change);
        model.Close(); Check(f.Cancelled == 1 && f.Accepted == 0 && f.Repairs.Count == 0 && f.Recovery.Calls.Count == 0
            && f.Recovery.Trace.Count(x => x == "manifest") == captures, "Stale mismatch performed effects or repeated cancellation.");
    }
    private static void Reentry(string phase)
    {
        var f = new Fixture(); var model = f.Create();
        if (phase == "capture") f.Recovery.Hook = at => { if (at == "manifest") f.Recovery.Change("login"); };
        if (phase == "text") f.OnText = () => f.Recovery.Change("login");
        bool opened = model.Open();
        if (phase is "capture" or "text")
        { Check(!opened && model.Report is null && f.Cancelled == 1, "Report adopted reentrant identity."); return; }
        if (phase == "repair")
        {
            f.OnApply = () => { Check(!model.Apply(ClientCompatibilityRepair.Manifest), "Repair reentered itself."); f.Recovery.Change("login"); };
            Check(model.Apply(ClientCompatibilityRepair.Manifest) && model.Status == "" && f.Repairs.Count == 1 && f.Cancelled == 1, "Repair published into new owner.");
        }
        else if (phase == "order")
        { f.OnOrder = () => f.Recovery.Change("login"); Check(model.PrepareOverride() is null && f.Recovery.Calls.Count == 0, "Order capture returned stale override."); }
        else
        {
            f = new(); f.Language(); model = f.Create(); model.Open();
            Check(model.Continue(() => f.Recovery.Change("login")) && f.Accepted == 0 && f.Cancelled == 1, "Window close continued another login.");
            f = new(); f.Language(); model = f.Create(); model.Open();
            try { model.Continue(() => throw new InvalidOperationException("close")); throw new Exception("No close failure."); }
            catch (InvalidOperationException ex) when (ex.Message == "close") { }
            model.Close(); Check(f.Cancelled == 1 && f.Accepted == 0, "Failed close leaked or repeated cancellation.");
        }
    }
    private static async Task Override(string? fault)
    {
        var f = new Fixture(); var model = f.Create(); model.Open();
        Check(!f.Recovery.Access.IsAdministrator, "Recovery fixture unexpectedly logged in as administrator.");
        f.Recovery.Change("snapshot"); Check(model.IsCurrent, "Snapshot progression invalidated warning.");
        var intent = model.PrepareOverride()!; Check(!intent.RequiresWarning, "Matching normalized order warned.");
        var busy = f.Recovery.Access.BeginCredentials(out _)!;
        Check(!intent.CanStart && intent.StartAsync(null, (_, _) => { }) is null, "Override stole another credential admission.");
        f.Recovery.Access.End(busy);
        f.Recovery.FaultPhase = "override"; f.Recovery.Fault = fault;
        var outcomes = new List<bool>();
        var work = intent.StartAsync(null, (_, accepted) => outcomes.Add(accepted))!;
        if (fault is null) model.Close();
        Check(intent.StartAsync(null, (_, _) => { }) is null, "Override intent ran twice.");
        await f.Recovery.Ports.PumpUntil(work);
        Check(outcomes.Single() == (fault is null) && !f.Recovery.Access.Busy && f.Recovery.Calls.Count == (fault is null ? 3 : 1),
            "Recovery lost existing transport ordering or close cancelled admitted work.");
        if (fault is not null)
        {
            f.Recovery.Fault = null;
            await f.Recovery.Ports.PumpUntil(model.PrepareOverride()!.StartAsync(null, (_, accepted) => outcomes.Add(accepted))!);
            Check(outcomes.SequenceEqual(new[] { false, true }) && f.Recovery.Calls.Count == 4, "Explicit recovery retry failed.");
        }
    }
    private static async Task OrderChange()
    {
        var f = new Fixture(); var model = f.Create(); model.Open();
        var intent = model.PrepareOverride()!;
        f.Order = new[] { "different" };
        Check(intent.StartAsync(null, (_, _) => { }) is null && f.Recovery.Calls.Count == 0, "Confirmation authorized another local mod order.");
        intent = model.PrepareOverride()!; Check(intent.RequiresWarning, "Different local mod order did not warn.");
        await f.Recovery.Ports.PumpUntil(intent.StartAsync(null, (_, _) => { })!);
        Check(f.Recovery.Calls.Count == 3, "Confirmed new order did not execute original override pipeline.");
    }

    private sealed class Fixture : IClientCompatibilityReportText, IClientCompatibilityRepairRuntime, IClientCompatibilityModOrderSource
    {
        internal readonly ClientCompatibilityOverrideTests.Fixture Recovery = new();
        internal readonly ModLoginResponseDto Response;
        internal string OriginalServer => Json(Manifest() with { DlcIds = new[] { "Ideology" }, ConfigSha256 = "different" });
        internal int Accepted, Cancelled, Closed, Diagnostics;
        internal bool Caller = true;
        internal string[]? Order = new[] { " MOD.I_steam " };
        internal Action? OnText, OnApply, OnOrder, OnCurrent;
        internal readonly List<(ClientCompatibilityRepair Kind, string Manifest)> Repairs = new();
        internal Fixture()
        { Recovery.Manifest = Json(Manifest()); Response = ClientCompatibilityMismatchTests.Response(Manifest() with { DlcIds = new[] { "Ideology" }, ConfigSha256 = "different" }); }
        internal void Language()
        { Response.Result!.Accepted = true; Response.CompatibilityIssues = new() { Issue("GameLanguageMismatch", "Warning"), Issue("AllowedPureTranslationMod", "Info") }; }
        internal ClientCompatibilityMismatch Create(bool accept = true) => new(Recovery.Session, Recovery.Ports, Recovery.Access,
            Recovery.Flow, Recovery, this, this, this, Response, "ChineseSimplified", accept ? () => Accepted++ : null, () => Cancelled++,
            () => { OnCurrent?.Invoke(); return Caller; });
        public string Text(string key, params (string Name, object? Value)[] args)
        { OnText?.Invoke(); return key + (args.Length == 0 ? "" : ":" + string.Join(",", args.Select(a => a.Value))); }
        public void Diagnose(string message) => Diagnostics++;
        public string Apply(ClientCompatibilityRepair repair, string manifestJson) { Repairs.Add((repair, manifestJson)); OnApply?.Invoke(); return "applied"; }
        public string SteamPostfix => "_steam";
        public IReadOnlyList<string>? ReadPackageOrder() { OnOrder?.Invoke(); return Order; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
