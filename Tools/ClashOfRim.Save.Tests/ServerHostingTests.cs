using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Hosting;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Configuration;

internal static class ServerHostingTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        string[] keys = ["CLASH_OF_RIM_SERVER_LANGUAGE", "CLASH_OF_RIM_DATA_DIR", "CLASH_OF_RIM_LOG_DIR",
            "CLASH_OF_RIM_MAX_REQUEST_BODY_SIZE_BYTES", "CLASH_OF_RIM_AUTH_DEBUG", "CLASH_OF_RIM_STEAM_WEB_API_KEY", "CLASH_OF_RIM_STEAM_APP_ID"];
        var environment = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        var culture = CultureInfo.CurrentCulture;
        var plugins = new ServerPluginRegistry([
            new("test.İ", "名字", "1.2", "测试.dll", "路径/插件.dll", ["pawn", "map"]),
            new("test.empty", "Empty", "2", "empty", "", [])]);
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string key in keys) Environment.SetEnvironmentVariable(key, null);
                Configuration();
                Information(new ServerInformation(plugins), plugins);
            }
            foreach (string key in keys) Environment.SetEnvironmentVariable(key, null);
            using var f = new HttpSnapshotRecoveryTests.Fixture(factory, plugins: plugins);
            f.Start();
            var hello = f.Client.GetFromJsonAsync<ServerHelloResponse>(ProtocolContractManifest.Find(ProtocolMessageKind.ServerHello).Route).GetAwaiter().GetResult()!;
            Check(hello.Result.Accepted && hello.Plugins.Select(p => p.Id).SequenceEqual(plugins.Plugins.Select(p => p.Id)),
                "Host hello did not use the supplied state's plugin catalogue.");
            var listed = f.Client.GetFromJsonAsync<JsonElement>("/server/plugins").GetAwaiter().GetResult();
            Check(listed.EnumerateObject().Select(p => p.Name).SequenceEqual(["plugins"]), "Plugin listing changed its envelope.");
            var entries = listed.GetProperty("plugins").EnumerateArray().ToArray();
            Check(entries.Length == plugins.Plugins.Count, "Plugin listing changed its membership.");
            for (int i = 0; i < entries.Length; i++)
            {
                Check(entries[i].EnumerateObject().Select(p => p.Name).Order().SequenceEqual(
                    new[] { "id", "name", "version", "assemblyName", "fileName", "capabilities" }.Order()), "Plugin listing changed its wire fields.");
                Check(entries[i].GetProperty("id").GetString() == plugins.Plugins[i].Id
                    && entries[i].GetProperty("fileName").GetString() == plugins.Plugins[i].FileName,
                    "Plugin listing lost Unicode identity or path.");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            foreach (var item in environment) Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    private static void Configuration()
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-config-名字");
        var empty = new ConfigurationBuilder().Build();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Localization:Language"] = "zh-CN", ["Persistence:DataDirectory"] = "configured-data",
            ["Server:MaxRequestBodySizeBytes"] = "4096", ["Authentication:DebugMode"] = "false",
            ["Authentication:SteamWebApiKey"] = " synthetic-config-key ", ["Authentication:SteamAppId"] = "42"
        }).Build();
        Check(ServerStartupConfiguration.DataDirectory(empty, root) == Path.Combine(root, "Data")
            && ServerStartupConfiguration.MaxRequestBodySize(empty) == 256L * 1024 * 1024, "Startup defaults changed.");
        var defaults = ServerStartupConfiguration.ServerConfiguration(empty);
        Check(defaults.AuthenticationDebugMode == ClashOfRimServerConfiguration.DefaultAuthenticationDebugMode
            && defaults.SteamAppId == ClashOfRimServerConfiguration.DefaultSteamAppId && defaults.SteamWebApiKey is null,
            "Authentication defaults changed.");
        Check(ClashOfRimNetworkServer.ResolveServerLanguage([], configuration) == ServerLocalization.ChineseSimplified
            && ClashOfRimNetworkServer.ResolveDataDirectory(configuration, root) == Path.Combine(root, "configured-data"),
            "Public startup helpers no longer select configured values.");
        Environment.SetEnvironmentVariable("CLASH_OF_RIM_SERVER_LANGUAGE", "en-US");
        Check(ServerStartupConfiguration.ResolveLanguage([], configuration) == ServerLocalization.English
            && ServerStartupConfiguration.ResolveLanguage(["--LANGUAGE=en-US", "--LOCALE=zh-CN"], configuration) == ServerLocalization.ChineseSimplified,
            "Environment or last command-line language precedence changed.");
        Check(ServerStartupConfiguration.ResolveLanguage(["--language="], configuration) == ServerLocalization.DetectOperatingSystemLanguage(),
            "An empty explicit language no longer selects the OS fallback.");
        Environment.SetEnvironmentVariable("CLASH_OF_RIM_DATA_DIR", "environment-data");
        Check(ServerStartupConfiguration.DataDirectory(configuration, root) == Path.Combine(root, "environment-data"), "Relative environment path changed.");
        Environment.SetEnvironmentVariable("CLASH_OF_RIM_DATA_DIR", root);
        Check(ServerStartupConfiguration.DataDirectory(configuration, "other") == root, "Absolute environment path was rebased.");
        Environment.SetEnvironmentVariable("CLASH_OF_RIM_LOG_DIR", "relative-log");
        Check(Path.GetDirectoryName(ServerStartupConfiguration.LogFilePath(root)) == "relative-log", "Log directory semantics changed.");
        foreach (string value in new[] { "-1", "0", "invalid", "9223372036854775808" })
        {
            Environment.SetEnvironmentVariable("CLASH_OF_RIM_MAX_REQUEST_BODY_SIZE_BYTES", value);
            Check(ServerStartupConfiguration.MaxRequestBodySize(configuration) == 256L * 1024 * 1024,
                "Invalid nonempty environment limit incorrectly fell back to configured limit.");
        }
        Environment.SetEnvironmentVariable("CLASH_OF_RIM_MAX_REQUEST_BODY_SIZE_BYTES", "8192");
        Check(ServerStartupConfiguration.MaxRequestBodySize(configuration) == 8192, "Valid environment limit was ignored.");
        Environment.SetEnvironmentVariable("CLASH_OF_RIM_AUTH_DEBUG", "true");
        Environment.SetEnvironmentVariable("CLASH_OF_RIM_STEAM_WEB_API_KEY", " synthetic-env-key ");
        Environment.SetEnvironmentVariable("CLASH_OF_RIM_STEAM_APP_ID", "84");
        var overridden = ServerStartupConfiguration.ServerConfiguration(configuration);
        Check(overridden.AuthenticationDebugMode && overridden.SteamAppId == 84 && overridden.SteamWebApiKey == "synthetic-env-key",
            "Authentication environment overrides or trimming changed.");
        foreach (string key in new[] { "CLASH_OF_RIM_AUTH_DEBUG", "CLASH_OF_RIM_STEAM_WEB_API_KEY", "CLASH_OF_RIM_STEAM_APP_ID",
            "CLASH_OF_RIM_MAX_REQUEST_BODY_SIZE_BYTES", "CLASH_OF_RIM_DATA_DIR", "CLASH_OF_RIM_SERVER_LANGUAGE" })
            Environment.SetEnvironmentVariable(key, " ");
        var configured = ServerStartupConfiguration.ServerConfiguration(configuration);
        Check(!configured.AuthenticationDebugMode && configured.SteamAppId == 42 && configured.SteamWebApiKey == "synthetic-config-key"
            && ServerStartupConfiguration.MaxRequestBodySize(configuration) == 4096
            && ServerStartupConfiguration.DataDirectory(configuration, root) == Path.Combine(root, "configured-data")
            && ServerStartupConfiguration.ResolveLanguage([], configuration) == ServerLocalization.ChineseSimplified,
            "Whitespace environment values stopped falling back to configuration.");
    }

    private static void Information(ServerInformation information, ServerPluginRegistry plugins)
    {
        var before = DateTimeOffset.UtcNow;
        var hello = information.Hello();
        Check(hello.Result.Accepted && hello.ProductName == ClashOfRimVersion.ProductName && hello.ProductVersion == ClashOfRimVersion.ProductVersion
            && hello.ProtocolVersion == ProtocolApiVersion.Current && hello.ProtocolMajor == ProtocolApiVersion.Major
            && hello.ProtocolMinor == ProtocolApiVersion.Minor && hello.MinimumSupportedProtocolMajor == ProtocolApiVersion.MinimumSupportedMajor
            && hello.MinimumSupportedProtocolMinor == ProtocolApiVersion.MinimumSupportedMinor && hello.CompatibilityApiVersion == ClashOfRimVersion.CompatibilityApiVersion,
            "Hello version contract changed.");
        Check(DateTimeOffset.TryParseExact(hello.ServerTimeUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            && time >= before && time <= DateTimeOffset.UtcNow, "Hello timestamp became culture-dependent or stale.");
        Check(hello.Plugins.Select(p => p.Id).SequenceEqual(plugins.Plugins.Select(p => p.Id))
            && information.ListPlugins().Plugins.Select(p => p.FileName).SequenceEqual(plugins.Plugins.Select(p => p.FileName)),
            "Metadata projection changed plugin order or contents.");
        Check(new ServerInformation(new ServerPluginRegistry()).Hello().Plugins.Count == 0, "Empty catalogue advertised plugins.");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
