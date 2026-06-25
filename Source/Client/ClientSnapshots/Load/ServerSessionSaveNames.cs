using System.Text;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

public static class ServerSessionSaveNames
{
    private const string ServerSavePrefix = "ClashOfRim_server_";

    public static string BuildSnapshotSaveName(string userId, string colonyId, string? snapshotId)
    {
        return ServerSavePrefix
            + NormalizeSaveNamePart(userId)
            + "_"
            + NormalizeSaveNamePart(colonyId)
            + "_"
            + NormalizeSaveNamePart(snapshotId);
    }

    public static string NormalizeSaveNamePart(string? value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "unknown" : value!.Trim();
        var builder = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        }

        string normalized = builder.ToString();
        return normalized.Length <= 48 ? normalized : normalized.Substring(0, 48);
    }
}
