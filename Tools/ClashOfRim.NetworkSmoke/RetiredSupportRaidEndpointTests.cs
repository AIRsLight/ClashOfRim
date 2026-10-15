using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;

internal static class RetiredSupportRaidEndpointTests
{
    public static async Task RunAsync(HttpClient http, ClashOfRimNetworkState state, string senderToken, string targetToken,
        string supportId, string raidId, PawnExchangePackageDto pawnPackage)
    {
        var support = state.Ledger.Find(supportId)!;
        var supportPayload = (SupportPawnEventPayload)support.Payload;
        var raid = state.Ledger.Find(raidId)!;
        // A rejected legacy raid request must not run timeout recovery before returning.
        var expired = AuthoritativeEventFactory.Create(ServerEventType.Raid,
            new EventParty("retired-raid-attacker", "retired-colony-a"), new EventParty("retired-raid-defender", "retired-colony-b"),
            "retired-raid:expired", false, new RaidEventPayload("old-defender", null, DateTimeOffset.UnixEpoch, null, null),
            DateTimeOffset.UnixEpoch, new EventTargetContext("RetiredWorld", "RetiredMap", 1, EventLandingMode.MapEdge));
        state.Ledger.Append(expired);
        string ledger = JsonSerializer.Serialize(state.Ledger.ListAll()), snapshots = JsonSerializer.Serialize(state.SnapshotStore.ListLatest());
        var versions = new[] { "user-a", "user-b", "retired-raid-attacker", "retired-raid-defender" }
            .ToDictionary(u => u, state.EventNotifications.GetVersion);

        void Unchanged()
        {
            if (ledger != JsonSerializer.Serialize(state.Ledger.ListAll()) || snapshots != JsonSerializer.Serialize(state.SnapshotStore.ListLatest())
                || versions.Any(p => state.EventNotifications.GetVersion(p.Key) != p.Value))
                throw new Exception("Retired support/raid endpoint changed ledger, snapshots or notifications.");
        }

        foreach (bool isRaid in new[] { true, false })
        {
            string route = ProtocolContractManifest.Find(isRaid ? ProtocolMessageKind.CreateRaid : ProtocolMessageKind.CreateSupportPawn).Route;
            foreach (string key in new[] { "retired-support-raid:new", isRaid ? raid.IdempotencyKey : support.IdempotencyKey })
            foreach (string? token in new[] { null, "invalid-token", senderToken, targetToken })
            foreach (string mode in isRaid ? new[] { "Player", "VanillaNpc", "999", "unknown" } : new[] { "temporary", "permanent", "normal" })
            {
                object request = isRaid
                    ? new CreateRaidRequest(key, new ProtocolIdentity(raid.Actor.UserId, raid.Actor.ColonyId, ((RaidEventPayload)raid.Payload).AttackForce!.AttackerSnapshotId),
                        new ProtocolIdentity(raid.Target.UserId, raid.Target.ColonyId, ((RaidEventPayload)raid.Payload).DefenderSnapshotId),
                        true, false, 1000, null, null, raid.TargetContext!.WorldObjectId!, raid.TargetContext.MapUniqueId!,
                        ((RaidEventPayload)raid.Payload).DefenderSnapshotId, Array.Empty<string>(), Array.Empty<ThingReferenceDto>(), opponentKind: mode)
                    : new CreateSupportPawnRequest(key, new ProtocolIdentity(support.Actor.UserId, support.Actor.ColonyId, supportPayload.SourceSnapshotId),
                        new ProtocolIdentity(support.Target.UserId, support.Target.ColonyId, null),
                        supportPayload.PawnGlobalKey, supportPayload.SourceSnapshotId, "retired support", mode == "temporary", null,
                        pawnPackage.Reference, pawnPackage,
                        new EventTargetContextDto(support.TargetContext!.WorldObjectId, support.TargetContext.MapUniqueId, support.TargetContext.Tile, "MapEdge"),
                        permanentSupport: mode == "permanent", supportDurationDays: 7);
                using var message = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(request, request.GetType()) };
                if (token != null) message.Headers.Add(MultipartSnapshotTransport.AuthenticationHeaderName, token);
                using var response = await http.SendAsync(message);
                Unchanged();
                await Rejected(response, isRaid);
            }
            foreach (string body in new[] { "", "null", "{}", "not-json" })
            {
                using var response = await http.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"));
                Unchanged();
                await Rejected(response, isRaid);
            }
        }
        state.Ledger.ChangeStatus(expired.EventId, ServerEventStatus.Cancelled);
        Console.WriteLine("通过：退役支援/袭击入口的 64 个请求均拒绝，且不触发过期袭击处理或其他状态变化");
    }

    private static async Task Rejected(HttpResponseMessage response, bool raid)
    {
        var value = await response.Content.ReadFromJsonAsync<EventCreationResponse>();
        if (response.StatusCode != HttpStatusCode.OK || value?.Result.Accepted != false
            || value.Result.ErrorCode != ProtocolErrorCode.ServerRejected || string.IsNullOrWhiteSpace(value.Result.Message)
            || value.EventId != null || value.AppliedSnapshotId != null || value.NextLineageToken != null
            || value.RaidStartedAtUtc != null || value.RaidDeadlineUtc != null || value.RaidFinalDeadlineUtc != null)
            throw new Exception("Retired support/raid endpoint accepted or disclosed an operation: " + (raid ? "raid" : "support"));
    }
}
