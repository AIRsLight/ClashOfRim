using System;

namespace AIRsLight.ClashOfRim.ClientNetwork;

public static class ClashOfRimServerUrlUtility
{
    public static string NormalizeHttpBaseUrl(string? serverBaseUrl)
    {
        string normalized = (serverBaseUrl ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        // Identify the supplied scheme before trimming trailing slashes. Otherwise
        // an empty authority such as "http://" can become the bare host "http:".
        if (normalized.StartsWith("//", StringComparison.Ordinal)) normalized = "http:" + normalized;
        else if (normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) normalized = "http://" + normalized.Substring("ws://".Length);
        else if (normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) normalized = "https://" + normalized.Substring("wss://".Length);
        else if (!normalized.Contains("://")) normalized = "http://" + normalized;
        return normalized.TrimEnd('/');
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
