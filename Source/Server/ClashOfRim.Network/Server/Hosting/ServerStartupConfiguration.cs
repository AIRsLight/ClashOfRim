using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Configuration;

namespace AIRsLight.ClashOfRim.Network.Hosting;

internal static class ServerStartupConfiguration
{
    private const long DefaultMaxRequestBodySizeBytes = 256L * 1024L * 1024L;

    internal static string ResolveLanguage(string[] args, IConfiguration configuration)
    {
        string? language = ReadSetting(configuration, "CLASH_OF_RIM_SERVER_LANGUAGE", "Localization:Language");

        foreach (string arg in args ?? Array.Empty<string>())
        {
            if (arg.StartsWith("--language=", StringComparison.OrdinalIgnoreCase))
            {
                language = arg["--language=".Length..];
            }
            else if (arg.StartsWith("--locale=", StringComparison.OrdinalIgnoreCase))
            {
                language = arg["--locale=".Length..];
            }
        }

        return string.IsNullOrWhiteSpace(language)
            ? ServerLocalization.DetectOperatingSystemLanguage()
            : ServerLocalization.NormalizeLanguage(language);
    }

    internal static string LogFilePath(string contentRootPath)
    {
        string? configuredDirectory = Environment.GetEnvironmentVariable("CLASH_OF_RIM_LOG_DIR");
        string logDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(contentRootPath, "Logs")
            : configuredDirectory;

        return Path.Combine(logDirectory, $"server-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    internal static string DataDirectory(IConfiguration configuration, string contentRootPath)
    {
        string? configuredDirectory = ReadSetting(configuration, "CLASH_OF_RIM_DATA_DIR", "Persistence:DataDirectory");

        string dataDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(contentRootPath, "Data")
            : configuredDirectory!;
        return Path.IsPathRooted(dataDirectory)
            ? dataDirectory
            : Path.Combine(contentRootPath, dataDirectory);
    }

    internal static ClashOfRimServerConfiguration ServerConfiguration(IConfiguration configuration)
    {
        return new ClashOfRimServerConfiguration(
            authenticationDebugMode: LoadAuthenticationDebugMode(configuration),
            steamWebApiKey: LoadSteamWebApiKey(configuration),
            steamAppId: LoadSteamAppId(configuration));
    }

    internal static long MaxRequestBodySize(IConfiguration configuration)
    {
        string? value = ReadSetting(configuration, "CLASH_OF_RIM_MAX_REQUEST_BODY_SIZE_BYTES", "Server:MaxRequestBodySizeBytes");

        return long.TryParse(value, out long parsed) && parsed > 0
            ? parsed
            : DefaultMaxRequestBodySizeBytes;
    }

    private static bool LoadAuthenticationDebugMode(IConfiguration configuration)
    {
        string? value = ReadSetting(configuration, "CLASH_OF_RIM_AUTH_DEBUG", "Authentication:DebugMode");
        return bool.TryParse(value, out bool parsed) ? parsed : ClashOfRimServerConfiguration.DefaultAuthenticationDebugMode;
    }

    private static string? LoadSteamWebApiKey(IConfiguration configuration)
    {
        string? value = ReadSetting(configuration, "CLASH_OF_RIM_STEAM_WEB_API_KEY", "Authentication:SteamWebApiKey");

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static uint LoadSteamAppId(IConfiguration configuration)
    {
        string? value = ReadSetting(configuration, "CLASH_OF_RIM_STEAM_APP_ID", "Authentication:SteamAppId");

        return uint.TryParse(value, out uint parsed) && parsed > 0
            ? parsed
            : ClashOfRimServerConfiguration.DefaultSteamAppId;
    }

    private static string? ReadSetting(IConfiguration configuration, string environmentKey, string configurationKey)
    {
        string? value = Environment.GetEnvironmentVariable(environmentKey);
        return string.IsNullOrWhiteSpace(value) ? configuration[configurationKey] : value;
    }
}
