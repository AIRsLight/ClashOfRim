using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal static class RemoteMapQuestIsolation
{
    internal static void Sanitize(XElement root, string scope)
    {
        foreach (var leaf in root.Descendants().Where(e => !e.HasElements))
        {
            string value = leaf.Value.Trim();
            if (value.TrimStart('@').StartsWith("Quest_", StringComparison.Ordinal)
                && int.TryParse(value.TrimStart('@').Substring(6), out _))
            {
                leaf.Value = "null";
                continue;
            }
            string name = leaf.Name.LocalName;
            string parent = leaf.Parent?.Name.LocalName ?? "";
            bool signal = name.EndsWith("Signal", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("SignalTag", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("inSignal", StringComparison.Ordinal)
                || name.StartsWith("outSignal", StringComparison.Ordinal)
                || parent.EndsWith("SignalTags", StringComparison.OrdinalIgnoreCase)
                || name == "questTag" || parent == "questTags";
            if (signal && value.Length > 0 && value != "null" && !bool.TryParse(value, out _)
                && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                leaf.Value = "ClashOfRim.RemoteSignal." + scope + "." + value;
        }
    }
}
