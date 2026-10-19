using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json;

internal static class SnapshotDownloadTestClient
{
    public static Reply Download(SnapshotDownloads downloads, IColonySnapshotIndexStore snapshots, bool payload, string token, string? scope)
    {
        var result = payload
            ? SnapshotEndpoints.DownloadLatestSnapshotPayload(new DownloadLatestSnapshotPayloadRequest("attacker", "colony-a",
                snapshots.GetLatest("attacker", "colony-a")!.Identity.SnapshotId!, token, authorizationScope: scope), downloads)
            : SnapshotEndpoints.DownloadLatestSnapshot(new DownloadLatestSnapshotRequest("attacker", "colony-a", token, authorizationScope: scope), downloads);
        return ReadReply(result);
    }

    public sealed record Reply(bool Accepted, int HttpStatus, ProtocolErrorCode? ErrorCode,
        string? SnapshotId = null, string? Lineage = null, byte[]? Bytes = null, ActiveRaidRecoveryDto? Recovery = null);

    public static Reply ReadReply(IResult result)
    {
        if (result is FileContentHttpResult file) return new(true, 200, null, Bytes: file.FileContents.ToArray());
        if (result is IValueHttpResult { Value: DownloadLatestSnapshotResponse metadata })
            return new(metadata.Result.Accepted, 200, metadata.Result.ErrorCode, metadata.SnapshotId, metadata.Package?.NextLineageToken, Recovery: metadata.ActiveRaidRecovery);
        var body = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new(false, ((IStatusCodeHttpResult)result).StatusCode!.Value,
            (ProtocolErrorCode)body.GetProperty("result").GetProperty("errorCode").GetInt32());
    }
}
