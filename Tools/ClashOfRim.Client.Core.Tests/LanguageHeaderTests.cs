using AIRsLight.ClashOfRim.Protocol;

internal static class LanguageHeaderTests
{
    internal static void Run()
    {
        ServerLocalization.Reset();
        ServerLocalization.MergeExternal("English", new Dictionary<string, string> { ["test"] = "en" });
        ServerLocalization.MergeExternal("ChineseSimplified", new Dictionary<string, string> { ["test"] = "zh" });
        ServerLocalization.MergeExternal("French (Français)", new Dictionary<string, string> { ["test"] = "fr" });
        ServerLocalization.SetDefaultLanguage("English");
        try
        {
            foreach (var (language, expected) in new[] {
                ("English", "en"), ("en-US", "en"), ("ChineseSimplified (简体中文)", "zh"), ("zh-CN", "zh"),
                ("French (Français)", "fr"), ("Turkish (Türkçe)", "en"), ("Unknown\r\nInjected: value", "en") })
            {
                string header = ServerLocalization.EncodeLanguageHeader(language);
                if (header.Any(c => c < 33 || c > 126)) throw new Exception("Non-ASCII/control header: " + language);
                using var request = new HttpRequestMessage();
                request.Headers.Add(ServerLocalization.LanguageHeader, header);
                using var scope = ServerLocalization.BeginRequest(header);
                if (ServerLocalization.Text("test") != expected) throw new Exception("Wrong localized header result: " + language);
            }
            using (ServerLocalization.BeginRequest("ChineseSimplified"))
                if (ServerLocalization.Text("test") != "zh") throw new Exception("Legacy ASCII header stopped working.");
        }
        finally { ServerLocalization.Reset(); }
    }
}
