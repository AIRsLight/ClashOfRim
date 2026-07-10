using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.NetworkServer;

internal static class ServerConfigurationFileBootstrapper
{
    private const string ConfigurationFileName = "appsettings.json";
    private const string TemplateFileName = "appsettings.example.json";
    private const string EmbeddedTemplateResourceName = "ClashOfRim.NetworkServer.appsettings.example.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public static void Ensure(string[] args)
    {
        string contentRootPath = ResolveContentRootPath(args);
        string configurationPath = Path.Combine(contentRootPath, ConfigurationFileName);
        string templatePath = Path.Combine(contentRootPath, TemplateFileName);
        JsonObject template = ReadTemplate(templatePath);

        if (!File.Exists(configurationPath))
        {
            ApplyDetectedLanguageDefault(template);
            WriteJson(configurationPath, template);
            Console.WriteLine(FormatBootstrapMessage(
                "Created server configuration file: {0}",
                "已创建服务器配置文件：{0}",
                configurationPath));
            return;
        }

        JsonObject configuration = ReadConfiguration(configurationPath);
        if (MergeMissingValues(configuration, template))
        {
            WriteJson(configurationPath, configuration);
            Console.WriteLine(FormatBootstrapMessage(
                "Updated server configuration file with missing defaults: {0}",
                "已为服务器配置文件补全缺失默认值：{0}",
                configurationPath));
        }
    }

    internal static string ResolveContentRootPath(string[] args)
    {
        string? configuredPath = ReadArgumentValue(args, "--contentRoot")
            ?? ReadArgumentValue(args, "--content-root");

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT")
                ?? Environment.GetEnvironmentVariable("CLASH_OF_RIM_CONTENT_ROOT");
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Directory.GetCurrentDirectory();
        }

        return Path.GetFullPath(configuredPath, Directory.GetCurrentDirectory());
    }

    private static string? ReadArgumentValue(string[] args, string key)
    {
        if (args.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(key.Length + 1)..];
            }

            if (string.Equals(arg, key, StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static JsonObject ReadTemplate(string templatePath)
    {
        if (File.Exists(templatePath))
        {
            return ReadObject(templatePath);
        }

        using Stream? templateStream = typeof(ServerConfigurationFileBootstrapper)
            .Assembly
            .GetManifestResourceStream(EmbeddedTemplateResourceName);
        if (templateStream is not null)
        {
            using StreamReader reader = new(templateStream);
            JsonNode? embeddedNode = JsonNode.Parse(reader.ReadToEnd());
            if (embeddedNode is JsonObject embeddedTemplate)
            {
                return embeddedTemplate;
            }

            throw new InvalidOperationException($"{EmbeddedTemplateResourceName} must contain a JSON object.");
        }

        return new JsonObject
        {
            ["Localization"] = new JsonObject
            {
                ["Language"] = "English"
            }
        };
    }

    private static JsonObject ReadConfiguration(string configurationPath)
    {
        JsonNode? node = JsonNode.Parse(File.ReadAllText(configurationPath));
        if (node is JsonObject configuration)
        {
            return configuration;
        }

        throw new InvalidOperationException($"{ConfigurationFileName} must contain a JSON object.");
    }

    private static JsonObject ReadObject(string path)
    {
        JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
        if (node is JsonObject value)
        {
            return value;
        }

        throw new InvalidOperationException($"{path} must contain a JSON object.");
    }

    private static bool MergeMissingValues(JsonObject target, JsonObject template)
    {
        bool changed = false;
        foreach (KeyValuePair<string, JsonNode?> entry in template)
        {
            if (!target.ContainsKey(entry.Key))
            {
                target[entry.Key] = entry.Value?.DeepClone();
                changed = true;
                continue;
            }

            if (target[entry.Key] is JsonObject targetObject
                && entry.Value is JsonObject templateObject)
            {
                changed |= MergeMissingValues(targetObject, templateObject);
            }
        }

        return changed;
    }

    private static void ApplyDetectedLanguageDefault(JsonObject configuration)
    {
        if (configuration["Localization"] is not JsonObject localization)
        {
            localization = new JsonObject();
            configuration["Localization"] = localization;
        }

        localization["Language"] = ServerLocalization.DetectOperatingSystemLanguage();
    }

    private static string FormatBootstrapMessage(string englishFormat, string chineseFormat, string value)
    {
        string language = ServerLocalization.DetectOperatingSystemLanguage();
        string format = string.Equals(language, ServerLocalization.ChineseSimplified, StringComparison.OrdinalIgnoreCase)
            ? chineseFormat
            : englishFormat;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, value);
    }

    private static void WriteJson(string path, JsonObject value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, value.ToJsonString(WriteOptions) + Environment.NewLine);
    }
}
