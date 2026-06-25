using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class ServerLocalizationScope : IDisposable
{
    private readonly string? previousLanguage;

    internal ServerLocalizationScope(string language)
    {
        previousLanguage = ServerLocalization.CurrentLanguage;
        ServerLocalization.CurrentLanguage = ServerLocalization.NormalizeLanguage(language);
    }

    public void Dispose()
    {
        ServerLocalization.CurrentLanguage = previousLanguage;
    }
}

public static class ServerLocalization
{
    public const string LanguageHeader = "X-ClashOfRim-Language";
    public const string English = "English";
    public const string ChineseSimplified = "ChineseSimplified";
    public const string Japanese = "Japanese";
    public const string Korean = "Korean";
    public const string Russian = "Russian";

    private static readonly AsyncLocal<string?> RequestLanguage = new();
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<string, string>> Resources = new(StringComparer.OrdinalIgnoreCase);
    private static bool loaded;
    private static string defaultLanguage = English;

    internal static string? CurrentLanguage
    {
        get => RequestLanguage.Value;
        set => RequestLanguage.Value = value;
    }

    public static void Reset()
    {
        lock (Sync)
        {
            Resources.Clear();
            loaded = false;
        }
    }

    public static void MergeExternal(string language, IReadOnlyDictionary<string, string> entries)
    {
        if (entries == null || entries.Count == 0)
        {
            return;
        }

        lock (Sync)
        {
            Merge(language, entries);
            loaded = true;
        }
    }

    public static void RequireReady()
    {
        lock (Sync)
        {
            if (!loaded || Resources.Count == 0)
            {
                throw new InvalidOperationException("No server localization files were loaded.");
            }

            if (!Resources.TryGetValue(English, out Dictionary<string, string>? english)
                || english.Count == 0)
            {
                throw new InvalidOperationException("English server localization file is required.");
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> resource in Resources)
            {
                if (string.Equals(resource.Key, English, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IReadOnlyList<string> missingKeys = english.Keys
                    .Where(key => !resource.Value.ContainsKey(key))
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToList();
                if (missingKeys.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Server localization file {resource.Key} is missing keys: {string.Join(", ", missingKeys)}");
                }
            }
        }
    }

    public static IReadOnlyCollection<string> LoadedLanguages
    {
        get
        {
            lock (Sync)
            {
                return Resources.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    public static bool HasLanguage(string language)
    {
        lock (Sync)
        {
            return Resources.ContainsKey(NormalizeLanguage(language));
        }
    }

    public static ServerLocalizationScope BeginRequest(string? language)
    {
        RequireReady();
        if (string.IsNullOrWhiteSpace(language))
        {
            return new ServerLocalizationScope(defaultLanguage);
        }

        string requestedLanguage = NormalizeLanguage(language);
        return new ServerLocalizationScope(HasLanguage(requestedLanguage) ? requestedLanguage : defaultLanguage);
    }

    public static void SetDefaultLanguage(string? language)
    {
        defaultLanguage = NormalizeLanguage(language);
    }

    public static string Text(string key)
    {
        RequireReady();
        string normalizedLanguage = NormalizeLanguage(CurrentLanguage ?? defaultLanguage);
        if (!HasLanguage(normalizedLanguage))
        {
            normalizedLanguage = HasLanguage(defaultLanguage) ? defaultLanguage : English;
        }

        if (TryGet(normalizedLanguage, key, out string? value))
        {
            return value!;
        }

        throw new KeyNotFoundException("Missing server localization key: " + key);
    }

    public static string Text(string key, IReadOnlyDictionary<string, string?> args)
    {
        string text = Text(key);
        foreach (KeyValuePair<string, string?> arg in args)
        {
            text = text.Replace("{" + arg.Key + "}", arg.Value ?? string.Empty);
        }

        return text;
    }

    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return English;
        }

        string normalized = (language ?? string.Empty).Trim();
        if (normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return ChineseSimplified;
        }

        if (normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOf("English", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return English;
        }

        if (normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOf("Japanese", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Japanese;
        }

        if (normalized.StartsWith("ko", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOf("Korean", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Korean;
        }

        if (normalized.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOf("Russian", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Russian;
        }

        return normalized;
    }

    public static string DetectOperatingSystemLanguage()
    {
        foreach (string? candidate in EnumerateOperatingSystemLanguageCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string normalized = NormalizeLanguage(candidate);
            if (IsBuiltInLanguage(normalized))
            {
                return normalized;
            }
        }

        return English;
    }

    private static IEnumerable<string?> EnumerateOperatingSystemLanguageCandidates()
    {
        yield return CultureInfo.CurrentUICulture.Name;
        yield return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        yield return CultureInfo.CurrentCulture.Name;
        yield return Environment.GetEnvironmentVariable("LANGUAGE");
        yield return Environment.GetEnvironmentVariable("LC_ALL");
        yield return Environment.GetEnvironmentVariable("LC_MESSAGES");
        yield return Environment.GetEnvironmentVariable("LANG");
    }

    private static bool IsBuiltInLanguage(string language)
    {
        return string.Equals(language, English, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, ChineseSimplified, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, Japanese, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, Korean, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, Russian, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGet(string language, string key, out string? value)
    {
        string normalized = NormalizeLanguage(language);
        if (Resources.TryGetValue(normalized, out Dictionary<string, string>? entries)
            && entries.TryGetValue(key, out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    private static void Merge(string language, IReadOnlyDictionary<string, string> entries)
    {
        language = NormalizeLanguage(language);
        if (!Resources.TryGetValue(language, out Dictionary<string, string>? target))
        {
            target = new Dictionary<string, string>(StringComparer.Ordinal);
            Resources[language] = target;
        }

        foreach (KeyValuePair<string, string> entry in entries)
        {
            target[entry.Key] = entry.Value;
        }
    }
}
