using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using System.Reflection;
using System.Text.Json;

internal static class SnapshotEndpointTestClient
{
    public static void LoadLocalization() => ServerLocalization.MergeExternal(ServerLocalization.English,
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "TestLocalization", "English.json")))!);

    public static async Task<T> Invoke<T>(string name, ClashOfRimNetworkState state, object request, byte[] payload, ProtocolIdentity principal)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        state.LoginSessions.TryCreate(principal.UserId, principal.ColonyId!, now, out string sessionId);
        string token = state.AuthTokens.Issue(principal.UserId, principal.UserId, principal.ColonyId!, sessionId, now);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(JsonSerializer.Serialize(request)), "request");
        multipart.Add(new ByteArrayContent(payload), "payload", "snapshot.gz");
        await using var body = new MemoryStream(await multipart.ReadAsByteArrayAsync());
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        context.Request.Headers[MultipartSnapshotAuthentication.HeaderName] = token;
        if (!MultipartSnapshotAuthentication.TryAuthorize(context.Request, state, now))
            throw new Exception("Fixture authentication failed.");
        MethodInfo endpoint = typeof(ClashOfRimNetworkServer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        IResult result = await (Task<IResult>)endpoint.Invoke(null, new object[] { context.Request, state })!;
        return (T)((IValueHttpResult)result).Value!;
    }
}
