using System;

namespace AIRsLight.ClashOfRim.ClientNetwork;

public static class ClashOfRimServerUrlUtility
{
    public static string NormalizeHttpBaseUrl(string? serverBaseUrl)
    {
        string normalized = (serverBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return "http:" + normalized;
        }

        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            return "http://" + normalized.Substring("ws://".Length);
        }

        if (normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + normalized.Substring("wss://".Length);
        }

        return normalized.Contains("://")
            ? normalized
            : "http://" + normalized;
    }

    public static bool TryNormalizeHttpBaseUrl(string? serverBaseUrl, out string normalizedServerBaseUrl)
    {
        normalizedServerBaseUrl = NormalizeHttpBaseUrl(serverBaseUrl);
        if (string.IsNullOrWhiteSpace(normalizedServerBaseUrl))
        {
            return false;
        }

        return Uri.TryCreate(normalizedServerBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    public static Uri BuildHttpBaseUri(string serverBaseUrl)
    {
        return new Uri(NormalizeHttpBaseUrl(serverBaseUrl).TrimEnd('/') + "/");
    }
}
