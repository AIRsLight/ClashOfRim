using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.Transport;

internal static class MultiplayerAdminConfigurationTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                RoundTrip(); InputValidation(); Rows();
                await Refresh(); await Save();
            }
            foreach (string change in new[] { "close", "user", "auth", "server", "colony", "login", "generation", "permission",
                "permission-return", "unavailable", "reset", "configuration", "missing" }) await Stale(change);
            await RemovedRows(); await Busy(); await CloseDuringSave();
            foreach (string fault in new[] { "network", "rejected", "exception" }) await Failure(fault);
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static void RoundTrip()
    {
        var source = Config(); var draft = new ClientAdminConfigurationDraft(source);
        Check(draft.IsValid, "Unedited configuration is invalid under " + CultureInfo.CurrentCulture.Name);
        Check(draft.TryBuild(out var built) && Json(built) == Json(source), "Configuration round-trip lost a field, precision, or nested data.");
        var changed = new ClientAdminConfigurationDraft(Config());
        foreach (var property in typeof(ModAdminConfigurationDto).GetProperties().Where(p => p.PropertyType == typeof(bool)))
        {
            var field = typeof(ClientAdminConfigurationDraft).GetField(property.Name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(changed, true);
        }
        Check(changed.TryBuild(out var enabled) && typeof(ModAdminConfigurationDto).GetProperties().Where(p => p.PropertyType == typeof(bool))
            .All(p => (bool)p.GetValue(enabled)!), "A toggle was not written back.");
        source.CompatibilityMods[0].Configs[0].Mode = "Ignore";
        source.FixedTradeFees[0].SilverPerUnit = 900;
        Check(draft.TryBuild(out built) && built.CompatibilityMods[0].Configs[0].Mode == "Enforce" && built.FixedTradeFees[0].SilverPerUnit == 4,
            "Source objects escaped into the editable draft.");
        built.CompatibilityMods[0].Role = "changed"; built.FixedTradeFees[0].ThingDefName = "changed";
        Check(draft.CompatibilityMods[0].Role == "Required" && draft.FixedTradeFees[0].ThingDefName == "A", "Build returned the live draft.");
    }

    private static void InputValidation()
    {
        var draft = new ClientAdminConfigurationDraft(Config());
        foreach (var field in typeof(ClientAdminConfigurationDraft).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(ClientAdminTextInput)))
        {
            var input = (ClientAdminTextInput)field.GetValue(draft)!; string previous = input.Text;
            input.Text = "unfinished";
            Check(!draft.IsValid && !draft.TryBuild(out _), "Invalid input passed validation/build: " + field.Name);
            input.Text = previous;
        }
        foreach (var input in new[] { draft.TradeBaseFeeRate, draft.RaidProtectionHours })
        foreach (string text in new[] { "", "NaN", "Infinity", "-Infinity", "1e999", "0,25" })
        {
            string previous = input.Text; input.Text = text;
            Check(!draft.IsValid && !draft.TryBuild(out _), "Non-finite/ambiguous numeric input was accepted: " + text); input.Text = previous;
        }
        draft.BankMinLoanSilver.Text = "2147483648"; Check(!draft.IsValid, "Integer overflow passed.");
        draft.BankMinLoanSilver.Text = "-1";
        foreach (var input in new[] { draft.BankInterestDurationMultiplierCurve, draft.MercenaryDurationMultiplierCurve })
        {
            foreach (string text in new[] { "-1:2", "1:-2", "1:NaN", "1:Infinity", "1:1e999", "days:2", "1:2:3" })
            { input.Text = text; Check(!draft.IsValid && !draft.TryBuild(out _), "Invalid curve passed: " + text); }
            input.Text = " 0:1;4=1.25 "; Check(draft.IsValid, "Supported curve syntax was rejected.");
            input.Text = " "; Check(draft.IsValid, "Empty curve compatibility changed.");
        }
        Check(draft.TryBuild(out var built) && built.BankMinLoanSilver == -1 && built.BankInterestDurationMultiplierCurve == "",
            "Editor changed server-owned range validation or curve trimming.");
        draft.TradeFeeStrategy = "hIGHEstSIDE";
        Check(draft.TryBuild(out built) && built.TradeFeeStrategy == "HighestSide", "Shared fee strategy normalization was bypassed.");
    }

    private static void Rows()
    {
        var source = Config(); source.BankOverduePenaltyStages = "malformed;2:Raid;4:mod.İ:0.12345679\r\n5:PsychicWhisper:2";
        var draft = new ClientAdminConfigurationDraft(source);
        Check(draft.PenaltyStages.Count == 3 && draft.PenaltyStages[0].Severity == "1", "Legacy stage parsing/default severity changed.");
        draft.FixedTradeFees.Add(new(" ", " ")); draft.FixedTradeFees.Add(new(" Custom ", "-5"));
        draft.PenaltyStages.Add(new(" ", " ", " "));
        Check(draft.TryBuild(out var built) && built.FixedTradeFees.Count == 3 && built.FixedTradeFees[2].ThingDefName == "Custom"
            && built.FixedTradeFees[2].SilverPerUnit == 0 && built.BankOverduePenaltyStages == "2:Raid:1,4:mod.İ:0.12345679,5:PsychicWhisper:2",
            "Rows lost blank-row, negative-fee, custom-incident, precision or invariant output semantics.");
        var fee = draft.FixedTradeFees[0]; string amount = fee.SilverPerUnit;
        fee.SilverPerUnit = "x"; Check(!draft.IsValid && !draft.TryBuild(out _), "Invalid fixed fee passed."); fee.SilverPerUnit = amount;
        foreach (string value in new[] { "-1", "NaN", "Infinity", "1e999" })
        { draft.PenaltyStages[0].Severity = value; Check(!draft.IsValid && !draft.TryBuild(out _), "Invalid penalty severity passed."); }
        draft.PenaltyStages[0].Severity = "1"; draft.PenaltyStages[0].TriggerPenaltyCount = "0";
        Check(!draft.IsValid, "Nonpositive penalty count passed.");
    }

    private static async Task Refresh()
    {
        var f = Fixture(); var editor = Editor(f); Check(editor.Read() is null && f.Calls.IsEmpty, "Editor read fetched configuration.");
        await f.Ports.PumpUntil(f.Module.RefreshAsync()); var draft = editor.Read()!;
        draft.TradeOrderExpirationDays.Text = "unfinished";
        int calls = f.Calls.Count; await editor.SaveAsync(draft);
        Check(f.Calls.Count == calls && draft.TradeOrderExpirationDays.Text == "unfinished", "Invalid draft reached transport or was discarded.");
        editor.SelectModRole(draft, draft.CompatibilityMods[0])("Optional");
        editor.SelectConfigMode(draft, draft.CompatibilityMods[0], draft.CompatibilityMods[0].Configs[0])("Warn");
        Check(draft.CompatibilityMods[0].Role == "Optional" && draft.CompatibilityMods[0].Configs[0].Mode == "Warn"
            && f.Module.Snapshot!.Configuration!.CompatibilityMods[0].Role == "Required"
            && f.Module.Snapshot.Configuration.CompatibilityMods[0].Configs[0].Mode == "Enforce", "Manifest edits mutated the accepted cache.");
        f.ReadResponse.Configuration!.FixedTradeFees.Reverse();
        await f.Ports.PumpUntil(f.Module.RefreshAsync());
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture.Name == "fr-FR" ? "en-US" : "fr-FR");
            Check(ReferenceEquals(editor.Read(), draft) && draft.TradeOrderExpirationDays.Text == "unfinished"
                && draft.CompatibilityMods[0].Role == "Optional", "Identical refresh or culture change discarded edits.");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        foreach (string same in new[] { "game", "snapshot" }) f.Change(same);
        f.Access.SetAdministrator(true);
        Check(ReferenceEquals(editor.Read(), draft), "Account editor was tied to Game/snapshot or repeated permission observation.");
        f.ReadResponse.Configuration.CompatibilityMods[0].Configs[0].Mode = "Ignore";
        await f.Ports.PumpUntil(f.Module.RefreshAsync());
        Check(!ReferenceEquals(editor.Read(), draft) && editor.Read()!.CompatibilityMods[0].Configs[0].Mode == "Ignore",
            "Changed server manifest failed to replace the stale draft.");
    }

    private static async Task Save()
    {
        var f = Fixture(); var editor = Editor(f); await f.Ports.PumpUntil(f.Module.RefreshAsync()); var draft = editor.Read()!;
        draft.TradeOrderExpirationDays.Text = "19";
        editor.SelectFeeStrategy(draft)("HighestSide"); editor.AddFee(draft)("Steel");
        editor.AddPenalty(draft)("mod.Incident"); editor.SelectModRole(draft, draft.CompatibilityMods[0])("Optional");
        editor.SelectFeeThing(draft, draft.FixedTradeFees[0])("WoodLog");
        editor.SelectPenaltyKind(draft, draft.PenaltyStages[0])("CustomRaid");
        Check(draft.TradeFeeStrategy == "HighestSide" && draft.FixedTradeFees.Count == 3 && draft.FixedTradeFees[2].ThingDefName == "Steel"
            && draft.FixedTradeFees[0].ThingDefName == "WoodLog" && draft.PenaltyStages.Count == 2 && draft.PenaltyStages[1].Kind == "mod.Incident"
            && draft.PenaltyStages[0].Kind == "CustomRaid" && draft.CompatibilityMods[0].Role == "Optional", "Menu edits did not reach their selected draft rows.");
        Check(draft.TryBuild(out var expected), "Fixture edit invalid.");
        f.SaveResponse.Configuration = ClientProtocolJson.Copy(expected);
        var work = editor.SaveAsync(draft); var queued = await f.Ports.Next();
        draft.TradeOrderExpirationDays.Text = "99"; draft.CompatibilityMods[0].Configs[0].Mode = "Ignore";
        Check(Json(f.Sent!) == Json(expected) && f.Module.Snapshot!.Configuration!.TradeOrderExpirationDays != 19 && editor.Busy,
            "Save did not freeze edits or updated the accepted cache before publication.");
        queued(); await work;
        Check(!editor.Busy && f.Module.Snapshot!.Configuration!.TradeOrderExpirationDays == 19
            && editor.Read()!.TradeOrderExpirationDays.Text == "19" && !ReferenceEquals(editor.Read(), draft), "Accepted response did not replace the draft.");
    }

    private static async Task Stale(string change)
    {
        var f = Fixture(); var editor = Editor(f); await f.Ports.PumpUntil(f.Module.RefreshAsync()); var draft = editor.Read()!;
        var mod = draft.CompatibilityMods[0]; var config = mod.Configs[0]; var fee = draft.FixedTradeFees[0]; var penalty = draft.PenaltyStages[0];
        var actions = new[] { editor.SelectFeeStrategy(draft), editor.AddFee(draft), editor.SelectFeeThing(draft, fee), editor.AddPenalty(draft),
            editor.SelectPenaltyKind(draft, penalty), editor.SelectModRole(draft, mod), editor.SelectConfigMode(draft, mod, config) };
        string before = DraftJson(draft);
        switch (change)
        {
            case "close": editor.Close(); break;
            case "permission-return": f.Access.SetAdministrator(false); f.Access.SetAdministrator(true); await f.Ports.PumpUntil(f.Module.RefreshAsync()); break;
            case "reset": f.Module.Reset(); break;
            case "configuration": f.ReadResponse.Configuration!.TradeBaseFeeRate += 1; await f.Ports.PumpUntil(f.Module.RefreshAsync()); break;
            case "missing": f.ReadResponse.Configuration = null; await f.Ports.PumpUntil(f.Module.RefreshAsync()); break;
            default: f.Change(change); break;
        }
        // No intervening UI read: delayed menus/submission must normalize themselves.
        foreach (var action in actions) action("stale");
        Check(DraftJson(draft) == before, "Old draft menu crossed " + change);
        int calls = f.Calls.Count;
        await editor.SaveAsync(draft).WaitAsync(TimeSpan.FromSeconds(3));
        Check(DraftJson(draft) == before && f.Calls.Count == calls && !ReferenceEquals(editor.Read(), draft), "Old draft/menu crossed " + change);
    }

    private static async Task RemovedRows()
    {
        var f = Fixture(); var editor = Editor(f); await f.Ports.PumpUntil(f.Module.RefreshAsync()); var draft = editor.Read()!;
        var fee = draft.FixedTradeFees[0]; var penalty = draft.PenaltyStages[0]; var mod = draft.CompatibilityMods[0]; var config = mod.Configs[0];
        var feeEdit = editor.SelectFeeThing(draft, fee); var penaltyEdit = editor.SelectPenaltyKind(draft, penalty);
        var roleEdit = editor.SelectModRole(draft, mod); var modeEdit = editor.SelectConfigMode(draft, mod, config);
        draft.FixedTradeFees.Remove(fee); draft.PenaltyStages.Remove(penalty); mod.Configs.Remove(config);
        feeEdit("removed"); penaltyEdit("removed"); modeEdit("removed");
        draft.CompatibilityMods.Remove(mod); roleEdit("removed");
        Check(fee.ThingDefName == "A" && penalty.Kind == "Raid" && config.Mode == "Enforce" && mod.Role == "Required", "Detached row menu still edited its target.");
    }

    private static async Task Busy()
    {
        var f = Fixture(); var editor = Editor(f); await f.Ports.PumpUntil(f.Module.RefreshAsync()); var draft = editor.Read()!;
        var choose = editor.SelectFeeStrategy(draft); var ticket = f.Access.Begin(out _)!; int calls = f.Calls.Count;
        draft.TradeOrderExpirationDays.Text = "17"; choose("HighestSide"); await editor.SaveAsync(draft);
        Check(editor.Busy && f.Calls.Count == calls && draft.TradeFeeStrategy == "HighestSide" && draft.TradeOrderExpirationDays.Text == "17", "Editor bypassed shared admission or consumed input.");
        f.Access.End(ticket); choose("HighestSide");
        await f.Ports.PumpUntil(editor.SaveAsync(draft)); Check(f.Sent!.TradeOrderExpirationDays == 17, "Busy state prevented explicit later save.");
    }

    private static async Task CloseDuringSave()
    {
        var f = Fixture(); var editor = Editor(f); await f.Ports.PumpUntil(f.Module.RefreshAsync()); var draft = editor.Read()!;
        draft.TradeOrderExpirationDays.Text = "22"; f.SaveResponse.Configuration = Config(); f.SaveResponse.Configuration.TradeOrderExpirationDays = 22;
        var save = editor.SaveAsync(draft); var queued = await f.Ports.Next(); editor.Close();
        Check(editor.Busy && !ReferenceEquals(editor.Read(), draft), "Closing canceled account work or retained the old draft.");
        queued(); await save;
        Check(editor.Read()!.TradeOrderExpirationDays.Text == "22" && f.Notices.Count == 1, "Closed page prevented authoritative save publication.");
    }

    private static async Task Failure(string fault)
    {
        var f = Fixture(); var editor = Editor(f); await f.Ports.PumpUntil(f.Module.RefreshAsync()); var draft = editor.Read()!;
        f.FaultOperation = "save"; f.Fault = fault; draft.TradeOrderExpirationDays.Text = "25";
        await f.Ports.PumpUntil(editor.SaveAsync(draft));
        Check(!editor.Busy && ReferenceEquals(editor.Read(), draft) && draft.TradeOrderExpirationDays.Text == "25", "Failed save discarded corrections: " + fault);
        f.Fault = null; await f.Ports.PumpUntil(editor.SaveAsync(draft));
        Check(f.Sent!.TradeOrderExpirationDays == 25 && f.Notices.Count == 1, "Retry did not use the retained draft.");
    }

    private static ClientAdministrationTests.Fixture Fixture()
    { var f = new ClientAdministrationTests.Fixture(); f.ReadResponse.Configuration = Config(); return f; }
    private static ClientAdminConfigurationEditor Editor(ClientAdministrationTests.Fixture f) => new(f.Session, f.Access, f.Module);
    private static ModAdminConfigurationDto Config()
    {
        var config = new ModAdminConfigurationDto(); int index = 1;
        // Distinct values cover every scalar mapping and detect missing/new DTO fields.
        foreach (var p in typeof(ModAdminConfigurationDto).GetProperties())
        {
            if (p.PropertyType == typeof(int)) p.SetValue(config, index++);
            else if (p.PropertyType == typeof(float)) p.SetValue(config, index++ + 0.1234567f);
            else if (p.PropertyType == typeof(double)) p.SetValue(config, index++ + 0.123456789012345);
            else if (p.PropertyType == typeof(bool)) p.SetValue(config, false);
            else if (p.PropertyType == typeof(string)) p.SetValue(config, "value-" + p.Name);
        }
        config.TradeFeeStrategy = "Publisher";
        config.BankInterestDurationMultiplierCurve = "0:1;20:0.25"; config.MercenaryDurationMultiplierCurve = "0:1,10:0.5";
        config.BankOverduePenaltyStages = "2:Raid:0.12345679";
        config.FixedTradeFees = new() { new() { ThingDefName = "A", SilverPerUnit = 4 }, new() { ThingDefName = "İ", SilverPerUnit = 7 } };
        config.CompatibilityMods = new() { new() { PackageId = "mod.İ", Name = "模组-é", LoadOrder = 3, Role = "Required",
            Configs = new() { new() { FileName = "settings.xml", Mode = "Enforce", HasSavedFile = false } } } };
        return config;
    }
    private static string Json(ModAdminConfigurationDto value) => ClientProtocolJson.Serialize(value);
    private static string DraftJson(ClientAdminConfigurationDraft value)
    { Check(value.TryBuild(out var result), "Expected valid draft."); return Json(result); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
