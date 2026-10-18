using System.Text.Json;
using AIRsLight.ClashOfRim.Protocol;

namespace ClashOfRim.TestSupport;

internal static class ServerTestLocalization
{
    internal static void LoadEnglish() => ServerLocalization.MergeExternal(ServerLocalization.English,
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "TestLocalization", "English.json")))!);
}
