using System;
using System.Reflection;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Compatibility;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientProtocolEnvironment : ClientProtocolMetadata, IClientProtocolEnvironment
{
    public Action? CaptureSessionUnavailable(ClashOfRimClientNetworkContext context, string? sessionId) =>
        ClashOfRimModNetworkClient.CaptureSessionUnavailable?.Invoke(context, sessionId);
    public void MultipartDisposalFailed(string message) =>
        Log.Warning("[ClashOfRim] Ignored HTTP multipart dispose NullReferenceException: " + message);
    public string? HttpFailureHint(string route, int statusCode, bool incompatibleProtocol)
    {
        string? key = incompatibleProtocol ? "ClashOfRim.Network.ProtocolRejected"
            : statusCode is 502 or 503 or 504 ? "ClashOfRim.Network.ServiceUnavailable" : null;
        return key is null ? null : ClashOfRimText.Key(key, route.Named("ROUTE"),
            ($"{ClashOfRimVersion.ProductVersion} / {ClashOfRimVersion.ProtocolDisplayVersion} [{ClashOfRimVersion.ProtocolVersion}]").Named("CLIENT"));
    }
}

internal class ClientProtocolMetadata : IClientProtocolMetadata
{
    public string DeserializationFailure(string responseType) =>
        ClashOfRimText.Key("ClashOfRim.Network.StatusDeserializeFailed", responseType.Named("TYPE"));

    public string? CurrentLanguage
    {
        get
        {
            try
            {
                Type? database = Type.GetType("Verse.LanguageDatabase, Assembly-CSharp");
                object? active = database?.GetField("activeLanguage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    ?? database?.GetProperty("activeLanguage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null, null);
                if (active is null) return null;
                Type language = active.GetType();
                return language.GetField("folderName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(active) as string
                    ?? language.GetProperty("folderName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(active, null) as string
                    ?? language.GetProperty("FolderName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(active, null) as string;
            }
            catch { return null; }
        }
    }
}
