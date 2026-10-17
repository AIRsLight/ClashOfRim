using System;
using System.Reflection;
using AIRsLight.ClashOfRim.Modules.Transport;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientProtocolEnvironment : ClientProtocolMetadata, IClientProtocolEnvironment
{
    private readonly Action<string, string?> sessionExpired;
    internal ClientProtocolEnvironment(Action<string, string?> sessionExpired) => this.sessionExpired = sessionExpired;
    public void SessionExpired(string message, string? authToken) => sessionExpired(message, authToken);
    public void MultipartDisposalFailed(string message) =>
        Log.Warning("[ClashOfRim] Ignored HTTP multipart dispose NullReferenceException: " + message);
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
