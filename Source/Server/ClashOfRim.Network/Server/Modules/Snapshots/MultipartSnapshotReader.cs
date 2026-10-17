using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal static class MultipartSnapshotReader
{
    private static readonly JsonSerializerOptions MultipartJsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<MultipartSnapshotRequest<TRequest>?> Read<TRequest>(HttpRequest httpRequest)
    {
        if (!httpRequest.HasFormContentType)
        {
            return null;
        }

        IFormCollection form = await httpRequest.ReadFormAsync();
        string? requestJson = form["request"].FirstOrDefault();
        IFormFile? payloadFile = form.Files.GetFile("payload");
        if (string.IsNullOrWhiteSpace(requestJson) || payloadFile is null)
        {
            return null;
        }

        TRequest? request = JsonSerializer.Deserialize<TRequest>(requestJson, MultipartJsonOptions);
        if (payloadFile.Length < 0 || payloadFile.Length > int.MaxValue)
        {
            return null;
        }

        await using Stream stream = payloadFile.OpenReadStream();
        byte[] payload = new byte[(int)payloadFile.Length];
        int offset = 0;
        while (offset < payload.Length)
        {
            int read = await stream.ReadAsync(payload.AsMemory(offset, payload.Length - offset));
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return new MultipartSnapshotRequest<TRequest>(request, payload);
    }
}
