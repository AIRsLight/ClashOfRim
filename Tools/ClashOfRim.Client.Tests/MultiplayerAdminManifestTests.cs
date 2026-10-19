using AIRsLight.ClashOfRim.Modules.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.World;

internal static class MultiplayerAdminManifestTests
{
    internal static void Run() => RunAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Comparison(); await Dispatch(); await Audit();
            }
            foreach (string change in new[] { "close", "user", "server", "colony", "auth", "login", "generation", "permission", "permission-return", "unavailable", "configuration" })
                await Stale(change);
            await ChangedLocalOrder(); await Busy(); await SharedDrafts();
            foreach (string phase in new[] { "prepare", "confirm", "appearance" }) await CaptureReentry(phase);
            foreach (string fault in new[] { "network", "rejected", "exception" })
            { await OverrideFailure(fault); await WorldFailure(fault); }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Comparison()
    {
        var f = await Fixture(); var draft = f.Editor.Read()!;
        Check(!f.Model.PrepareOverride(draft)!.RequiresWarning, "Case, whitespace or Steam suffix changed normalized mod identity.");
        // Compare the accepted server list, never a locally edited draft pretending to be the baseline.
        draft.CompatibilityMods[0].PackageId = "unsaved";
        Check(!f.Model.PrepareOverride(draft)!.RequiresWarning, "Unsubmitted manifest edits became the accepted baseline.");
        foreach (var ids in new string[]?[] { new[] { "mod.b", "mod.i" }, new[] { "mod.i" }, new[] { "mod.i", "mod.b", "extra" }, new[] { "mod.ı", "mod.b" }, null })
        {
            f.Order.Ids = ids;
            Check(f.Model.PrepareOverride(draft)!.RequiresWarning, "Different/unknown mod list escaped the warning.");
        }
        f.Order.Postfix = ""; f.Order.Ids = new[] { "mod.i", "mod.b" };
        Check(f.Model.PrepareOverride(draft)!.RequiresWarning, "An absent Steam suffix stripped arbitrary text.");
        f.Order.Postfix = "_steam"; f.Order.Ids = Array.Empty<string>();
        f.Admin.ReadResponse.Configuration!.CompatibilityMods.Clear(); await f.Refresh();
        Check(!f.Model.PrepareOverride(f.Editor.Read()!)!.RequiresWarning, "Two empty manifests mismatched.");
    }
    private static async Task Dispatch()
    {
        var f = await Fixture(); var draft = f.Editor.Read()!;
        var intent = f.Model.PrepareOverride(draft)!;
        draft.TradeOrderExpirationDays.Text = "unfinished";
        f.Admin.Change("game"); f.Admin.Change("snapshot"); f.Admin.Access.SetAdministrator(true); await f.Refresh();
        Check(ReferenceEquals(draft, f.Editor.Read()) && intent.CanStart, "Account-level draft was tied to a Game/snapshot or identical refresh.");
        var progress = new List<float>(); var results = new List<bool>();
        Task? work = intent.StartAsync((_, value) => progress.Add(value), (_, accepted) => results.Add(accepted));
        Check(work is not null && intent.StartAsync(null, (_, _) => throw new InvalidOperationException("duplicate")) is null,
            "One confirmation was submitted repeatedly.");
        f.Editor.Close(); await f.Admin.Ports.PumpUntil(work!);
        Check(f.Override.Calls.SequenceEqual(new[] { "override", "requirements", "submit" }) && results.SequenceEqual(new[] { true })
            && progress.SequenceEqual(new[] { .12f, .24f, .56f, .72f, 1f }) && !f.Admin.Access.Busy,
            "Page close canceled admitted override or duplicated its catalog workflow.");
        Check(f.Override.Contexts.All(c => c.CurrentSnapshotId == "new") && f.Admin.Calls.All(c => c.Operation == "read"),
            "Override borrowed old context or invented an admin action/save.");
        Check(!intent.CanStart && intent.StartAsync(null, (_, _) => { }) is null, "Completed intent revived after close.");
        // World capture forwards the actual appearance and keeps its own capture/publication workflow.
        draft = f.Editor.Read()!; Task? world = f.Model.UpdateWorldAsync(draft);
        Check(world is not null && f.AppearanceReads == 1 && f.World.CapturedAppearance is { Mode: "Custom", IconDefName: "图标", ColorHex: "#123456" } && !ReferenceEquals(f.World.CapturedAppearance, f.Appearance), "World action bypassed appearance capture.");
        await f.Admin.Ports.PumpUntil(world!);
        Check(f.World.Calls.SequenceEqual(new[] { "metadata", "upload" }) && f.World.Successes == 1 && f.World.Markers == 1 && !f.Admin.Access.Busy,
            "World baseline lost its upload/publication or repeated follow-up work.");
    }
    private static async Task Stale(string change)
    {
        var f = await Fixture(); var draft = f.Editor.Read()!; var intent = f.Model.PrepareOverride(draft)!;
        var role = f.Editor.SelectModRole(draft, draft.CompatibilityMods[0]);
        if (change == "close") f.Editor.Close();
        else if (change == "configuration") { f.Admin.ReadResponse.Configuration!.TradeOrderExpirationDays++; await f.Refresh(); }
        else if (change == "permission-return") { f.Admin.Access.SetAdministrator(false); f.Admin.Access.SetAdministrator(true); await f.Refresh(); }
        else f.Admin.Change(change);
        role("Optional");
        Check(!intent.CanStart && intent.StartAsync(null, (_, _) => throw new InvalidOperationException("stale callback")) is null
            && f.Model.PrepareOverride(draft) is null && f.Model.UpdateWorldAsync(draft) is null && draft.CompatibilityMods[0].Role == "Required"
            && f.Override.Calls.Count == 0 && f.World.Calls.IsEmpty && f.AppearanceReads == 0, "Old manifest action crossed " + change);
    }
    private static async Task ChangedLocalOrder()
    {
        var f = await Fixture(); var intent = f.Model.PrepareOverride(f.Editor.Read()!)!;
        f.Order.Ids = new[] { "mod.b", "mod.i" };
        Check(intent.StartAsync(null, (_, _) => { }) is null && f.Override.Calls.Count == 0, "Confirmation authorized a different local mod order.");
        intent = f.Model.PrepareOverride(f.Editor.Read()!)!;
        Check(intent.RequiresWarning, "New local order did not require a new warning.");
        await f.Admin.Ports.PumpUntil(intent.StartAsync(null, (_, _) => { })!);
        Check(f.Override.Calls.Count == 3, "Explicit retry with the new local order failed.");
        f = await Fixture(); f.Order.Ids = null; intent = f.Model.PrepareOverride(f.Editor.Read()!)!;
        Check(intent.RequiresWarning, "Unavailable comparison silently bypassed warning.");
        await f.Admin.Ports.PumpUntil(intent.StartAsync(null, (_, _) => { })!);
        Check(f.Override.Calls.Count == 3, "Unknown comparison prevented credential recovery after explicit warning.");
    }
    private static async Task Busy()
    {
        var f = await Fixture(); var draft = f.Editor.Read()!; var intent = f.Model.PrepareOverride(draft)!;
        var held = f.Admin.Access.Begin(out _)!;
        Check(!intent.CanStart && intent.StartAsync(null, (_, _) => { }) is null && f.Model.PrepareOverride(draft) is null
            && f.Model.UpdateWorldAsync(draft) is null && f.AppearanceReads == 0, "Busy panel admitted work or consumed an existing intent.");
        f.Editor.SelectModRole(draft, draft.CompatibilityMods[0])("Optional");
        f.Admin.Access.End(held);
        Check(intent.CanStart && draft.CompatibilityMods[0].Role == "Optional", "Temporary busy state discarded edits or confirmation.");
        await f.Admin.Ports.PumpUntil(intent.StartAsync(null, (_, _) => { })!);
    }
    private static async Task SharedDrafts()
    {
        var f = await Fixture(); var draft = f.Editor.Read()!;
        var other = new ClientAdminConfigurationEditor(f.Admin.Session, f.Admin.Access, f.Admin.Module);
        Check(f.Model.PrepareOverride(other.Read()!) is null && f.Model.UpdateWorldAsync(other.Read()!) is null, "Manifest accepted another editor's draft.");
        f.Editor.SelectModRole(draft, draft.CompatibilityMods[0])("OptionalPureTranslation");
        f.Editor.SelectConfigMode(draft, draft.CompatibilityMods[0], draft.CompatibilityMods[0].Configs[0])("Warn");
        await f.Admin.Ports.PumpUntil(f.Editor.SaveAsync(draft));
        Check(f.Admin.Sent!.CompatibilityMods[0].Role == "OptionalPureTranslation" && f.Admin.Sent.CompatibilityMods[0].Configs[0].Mode == "Warn"
            && f.Admin.Calls.Select(c => c.Operation).SequenceEqual(new[] { "read", "save" }), "Manifest edits used a duplicate configuration submission path.");
    }
    private static async Task CaptureReentry(string phase)
    {
        var f = await Fixture(); var draft = f.Editor.Read()!; var intent = f.Model.PrepareOverride(draft)!;
        if (phase == "appearance") f.AppearanceHook = () => f.Admin.Change("server");
        else f.Order.Hook = () => f.Admin.Change("server");
        if (phase == "prepare") Check(f.Model.PrepareOverride(draft) is null, "Comparison reentry returned a stale intent.");
        else if (phase == "confirm") Check(intent.StartAsync(null, (_, _) => { }) is null, "Comparison reentry submitted new credentials.");
        else Check(f.Model.UpdateWorldAsync(draft) is null, "Appearance reentry submitted a different session.");
        Check(f.Override.Calls.Count == 0 && f.World.Calls.IsEmpty, "Capture reentry reached a transport.");
    }
    private static async Task OverrideFailure(string fault)
    {
        var f = await Fixture(); var draft = f.Editor.Read()!; var intent = f.Model.PrepareOverride(draft)!;
        f.Override.FaultPhase = "override"; f.Override.Fault = fault; var completed = new List<bool>();
        await f.Admin.Ports.PumpUntil(intent.StartAsync(null, (_, accepted) => completed.Add(accepted))!);
        Check(completed.SequenceEqual(new[] { false }) && !f.Admin.Access.Busy && f.Override.Calls.Count == 1 && !intent.CanStart
            && ReferenceEquals(f.Editor.Read(), draft), "Failed override lost completion/draft or reused confirmation.");
        f.Override.Fault = null;
        await f.Admin.Ports.PumpUntil(f.Model.PrepareOverride(draft)!.StartAsync(null, (_, accepted) => completed.Add(accepted))!);
        Check(completed.SequenceEqual(new[] { false, true }) && f.Override.Calls.Count == 4, "New confirmation failed to retry.");
    }
    private static async Task WorldFailure(string fault)
    {
        var f = await Fixture(); var draft = f.Editor.Read()!; f.World.FaultPhase = "metadata"; f.World.Fault = fault;
        await f.Admin.Ports.PumpUntil(f.Model.UpdateWorldAsync(draft)!);
        Check(!f.Admin.Access.Busy && f.World.Successes == 0 && ReferenceEquals(draft, f.Editor.Read()), "World failure leaked admission or changed configuration edits.");
        f.World.Fault = null; await f.Admin.Ports.PumpUntil(f.Model.UpdateWorldAsync(draft)!);
        Check(f.World.Successes == 1 && f.World.Markers == 1, "Explicit world retry duplicated/lost completion.");
    }
    private static async Task Audit()
    {
        var f = await Fixture(); var model = new ClientAdminAuditPanel(f.Admin.Session, f.Admin.Access, f.Admin.Module);
        Check(model.Read().Count == 0, "Empty accepted audit produced rows.");
        f.Admin.ReadResponse.AuditRecords = Enumerable.Range(0, 1500).Select(i => new ModAdminAuditRecordDto
        { CreatedAtUtc = "not-a-date-" + i, ActorUserId = "演员 İ", ActionKind = "custom:" + i, TargetUserId = i % 2 == 0 ? null : "玩家", Message = i % 3 == 0 ? "" : "说明 é" }).ToList();
        await f.Refresh(); var rows = model.Read(); var owner = model.Ownership;
        Check(rows.Count == 1500 && rows[0].ActionKind == "custom:0" && rows[1499].ActionKind == "custom:1499"
            && rows[0].CreatedAtUtc == "not-a-date-0" && rows[0].TargetUserId is null && rows[0].Message == "" && rows[1].Message == "说明 é"
            && ReferenceEquals(rows, model.Read()), "Audit projection reordered, parsed or rebuilt accepted records.");
        f.Admin.ReadResponse.AuditRecords[0].Message = "new";
        Check(model.Read()[0].Message == "", "Transport mutation changed accepted audit rows.");
        f.Admin.Change("game"); f.Admin.Change("snapshot"); Check(ReferenceEquals(rows, model.Read()), "Account audit was tied to Game/snapshot.");
        await f.Refresh(); Check(model.Read()[0].Message == "new" && rows[0].Message == "" && ReferenceEquals(owner, model.Ownership), "Audit refresh mutated an older projection or reset view ownership.");
        model.Close(); Check(model.Read().Count == 1500, "Closing a read-only audit cleared domain data.");
        f.Admin.Access.SetAdministrator(false); Check(model.Read().Count == 0 && !ReferenceEquals(owner, model.Ownership), "Revoked administrator retained audit data/view ownership.");
        f.Admin.Access.SetAdministrator(true); Check(model.Read().Count == 0, "Regrant revived removed server audit cache.");
        await f.Refresh(); Check(model.Read().Count == 1500, "Fresh authorization failed to read new audit state.");
        f.Admin.Change("unavailable"); Check(model.Read().Count == 0, "Unavailable session displayed audit state.");
        f = await Fixture(); model = new(f.Admin.Session, f.Admin.Access, f.Admin.Module);
        f.Admin.ReadResponse.AuditRecords.Add(new() { ActionKind = "old" }); await f.Refresh(); _ = model.Read();
        f.Admin.Change("user"); Check(model.Read().Count == 0, "Changed account retained old audit data.");
    }
    private static async Task<FixtureState> Fixture()
    {
        var f = new FixtureState(); await f.Refresh(); return f;
    }
    private sealed class OrderSource : IClientCompatibilityModOrderSource
    {
        internal string[]? Ids = { "mod.i", "MOD.B_STEAM" }; internal string Postfix = "_steam"; internal Action? Hook;
        public string SteamPostfix => Postfix;
        public IReadOnlyList<string>? ReadPackageOrder() { Hook?.Invoke(); return Ids; }
    }
    private sealed class FixtureState
    {
        internal readonly ClientAdministrationTests.Fixture Admin = new();
        internal readonly ClientCompatibilityOverrideTests.Fixture Override;
        internal readonly ClientWorldWritesTests.Fixture World;
        internal readonly OrderSource Order = new();
        internal readonly ClientAdminConfigurationEditor Editor;
        internal readonly ClientAdminManifestPanel Model;
        internal readonly ModColonyAppearanceDto Appearance = new() { Mode = "Custom", IconDefName = "图标", ColorHex = "#123456" };
        internal int AppearanceReads; internal Action? AppearanceHook;
        internal FixtureState()
        {
            Admin.ReadResponse.Configuration!.CompatibilityMods = new()
            {
                new() { PackageId = "mod.b", LoadOrder = 9, Configs = new() { new() { FileName = "配置.xml" } } },
                new() { PackageId = " MOD.I_StEaM ", LoadOrder = 1 }
            };
            Override = new(Admin); World = new(ClientWorldWriteKind.Baseline, Admin);
            Editor = new(Admin.Session, Admin.Access, Admin.Module);
            Model = new(Editor, Order, Override.Flow, World.Module, () => { AppearanceReads++; AppearanceHook?.Invoke(); return Appearance; });
        }
        internal Task Refresh() => Admin.Ports.PumpUntil(Admin.Module.RefreshAsync());
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
