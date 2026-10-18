using System;
using System.Globalization;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

// Each field declares its mapping once; validation and building use the same parser.
internal sealed class ClientAdminTextInput
{
    private delegate bool Parser<T>(string text, out T value);
    private readonly Func<string, ModAdminConfigurationDto?, bool> apply;
    internal string Text;
    private ClientAdminTextInput(string text, Func<string, ModAdminConfigurationDto?, bool> apply)
    { Text = text; this.apply = apply; }
    internal bool TryApply(ModAdminConfigurationDto? target) => apply(Text, target);
    private static ClientAdminTextInput Create<T>(string text, Parser<T> parse, Action<ModAdminConfigurationDto, T> write)
        => new(text, (input, target) =>
        {
            if (!parse(input, out var value)) return false;
            if (target is not null) write(target, value);
            return true;
        });
    internal static ClientAdminTextInput Number(int value, Action<ModAdminConfigurationDto, int> write)
        => Create<int>(value.ToString(CultureInfo.InvariantCulture), TryInt, write);
    internal static ClientAdminTextInput Number(float value, Action<ModAdminConfigurationDto, float> write)
        => Create<float>(value.ToString("R", CultureInfo.InvariantCulture), TryFloat, write);
    internal static ClientAdminTextInput Number(double value, Action<ModAdminConfigurationDto, double> write)
        => Create<double>(value.ToString("R", CultureInfo.InvariantCulture), TryDouble, write);
    internal static ClientAdminTextInput Curve(string value, Action<ModAdminConfigurationDto, string> write)
        => Create<string>(value, TryCurve, write);

    internal static bool TryInt(string text, out int value) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    internal static bool TryFloat(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
    internal static bool TryDouble(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static bool TryCurve(string text, out string value)
    {
        value = text.Trim();
        foreach (string part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pieces = part.Split(new[] { ':', '=' }, StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length != 2 || !TryInt(pieces[0], out int days) || days < 0
                || !TryFloat(pieces[1], out float multiplier) || multiplier < 0f) return false;
        }
        return true;
    }
}

internal sealed class ClientAdminFixedTradeFeeInput
{
    internal ClientAdminFixedTradeFeeInput(string thing, string silver) { ThingDefName = thing; SilverPerUnit = silver; }
    internal string ThingDefName, SilverPerUnit;
    internal bool TryRead(out ModAdminFixedTradeFeeDto? value)
    {
        value = null;
        string name = ThingDefName.Trim();
        if (name.Length == 0 && string.IsNullOrWhiteSpace(SilverPerUnit)) return true;
        if (name.Length == 0 || !ClientAdminTextInput.TryInt(SilverPerUnit, out int amount)) return false;
        value = new() { ThingDefName = name, SilverPerUnit = Math.Max(0, amount) };
        return true;
    }
}

internal sealed class ClientAdminPenaltyInput
{
    internal ClientAdminPenaltyInput(string trigger, string kind, string severity)
    { TriggerPenaltyCount = trigger; Kind = kind; Severity = severity; }
    internal string TriggerPenaltyCount, Kind, Severity;
    internal bool TryRead(out string? value)
    {
        value = null;
        string kind = Kind.Trim();
        if (kind.Length == 0 && string.IsNullOrWhiteSpace(TriggerPenaltyCount) && string.IsNullOrWhiteSpace(Severity)) return true;
        if (kind.Length == 0 || !ClientAdminTextInput.TryInt(TriggerPenaltyCount, out int count) || count <= 0
            || !ClientAdminTextInput.TryFloat(Severity, out float severity) || severity < 0) return false;
        value = count.ToString(CultureInfo.InvariantCulture) + ":" + kind + ":" + severity.ToString("R", CultureInfo.InvariantCulture);
        return true;
    }
}
