using System;

namespace AIRsLight.ClashOfRim.Modules.World;

// Keep the on-disk four-part URI-escaped format and its original account-key
// normalization. Changing network URL aliases must not silently rekey settings.
internal static class ClientColonyAppearanceCodec
{
    internal static string AccountKey(ClientColonyAppearanceAccount account)
    {
        string server = (account.Server ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        string world = (account.World ?? "").Trim(), user = (account.User ?? "").Trim(), colony = (account.Colony ?? "").Trim();
        if (server.Length == 0 || world.Length == 0 || user.Length == 0 || colony.Length == 0) return "";
        return Join(server, world, user, colony);
    }
    internal static string Encode(ColonyAppearanceSelection selection)
        => Join(selection.Mode, selection.IconDefName, selection.ColorDefName, selection.ColorHex);
    internal static ColonyAppearanceSelection Decode(string? packed)
    {
        if (string.IsNullOrWhiteSpace(packed)) return ColonyAppearanceSelection.Empty;
        string[] parts = packed!.Split('|');
        return parts.Length < 4 ? ColonyAppearanceSelection.Empty : new(Unescape(parts[0]), Unescape(parts[1]), Unescape(parts[2]), Unescape(parts[3]));
    }
    private static string Join(string a, string b, string c, string d)
        => Uri.EscapeDataString(a) + "|" + Uri.EscapeDataString(b) + "|" + Uri.EscapeDataString(c) + "|" + Uri.EscapeDataString(d);
    private static string Unescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }
}
