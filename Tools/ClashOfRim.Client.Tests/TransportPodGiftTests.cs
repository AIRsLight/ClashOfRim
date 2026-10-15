using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Protocol;
using RimWorld;
using RimWorld.Planet;
using Verse;

public static class TransportPodGiftTests
{
    public static void Run()
    {
        VerifyTransport(false).GetAwaiter().GetResult();
        VerifyTransport(true).GetAwaiter().GetResult();
    }

    private static async Task VerifyTransport(bool forced)
    {
        ClientSessionWork.Reset();
        using var handler = new Handler(forced);
        using var http = new HttpClient(handler);
        var client = new ClashOfRimModNetworkClient(http,
            new ClashOfRimClientNetworkContext("http://127.0.0.1:12345", "sender", "colony", "base", null, null, "token"));
        var snapshot = new ModSnapshotPackageMetadataDto { OwnerId = "sender", ColonyId = "colony", SnapshotId = "removed", PreviousSnapshotId = "base", LineageToken = "lineage" };
        var things = new[] { new ModThingReferenceDto { DefName = "WoodLog", StackCount = 5 } };
        var destination = new ModEventTargetContextDto { MapUniqueId = "map", WorldObjectId = "world", LandingMode = "DropPod" };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var result = await client.CreateGiftWithSnapshotAsync("gift-transport-pods:sender:base:pod", "receiver", "destination", null,
                things, null, destination, forced ? "Forced" : null, snapshot, new byte[] { 1, 2, 3 });
            if (attempt == 0 ? result.Success : !result.Success || result.Response?.Result?.Accepted != true || result.Response.AppliedSnapshotId != "removed")
                throw new Exception("Gift failed-response/retry outcome mismatch.");
        }
        if (handler.Calls != 2) throw new Exception("Unexpected retry count.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly bool forced;
        private string? originalRequest;
        public int Calls;
        public Handler(bool forced) => this.forced = forced;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != ProtocolContractManifest.Find(ProtocolMessageKind.CreateGiftWithSnapshot).Route
                || request.Headers.GetValues(MultipartSnapshotTransport.AuthenticationHeaderName).Single() != "token")
                throw new Exception("Gift must use the authenticated snapshot route.");
            var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
            string body = await parts["request"].ReadAsStringAsync(token);
            if (!(await parts["payload"].ReadAsByteArrayAsync(token)).SequenceEqual(new byte[] { 1, 2, 3 }) || (originalRequest != null && originalRequest != body))
                throw new Exception("Retry changed snapshot or gift metadata.");
            originalRequest = body;
            using var json = JsonDocument.Parse(body);
            var gift = json.RootElement.GetProperty("gift"); var snapshot = json.RootElement.GetProperty("confirmedSnapshot");
            if (gift.GetProperty("idempotencyKey").GetString() != "gift-transport-pods:sender:base:pod"
                || gift.GetProperty("actor").GetProperty("snapshotId").GetString() != "base"
                || gift.GetProperty("target").GetProperty("userId").GetString() != "receiver"
                || gift.GetProperty("things")[0].GetProperty("stackCount").GetInt32() != 5
                || gift.GetProperty("targetContext").GetProperty("landingMode").GetString() != "DropPod"
                || gift.GetProperty("deliveryKind").GetString() != (forced ? "Forced" : null)
                || snapshot.GetProperty("snapshotId").GetString() != "removed"
                || snapshot.GetProperty("previousSnapshotId").GetString() != "base"
                || snapshot.GetProperty("lineageToken").GetString() != "lineage"
                || snapshot.GetProperty("snapshotUploadKind").GetString() != ModSnapshotUploadKinds.GiftCreationConfirmation)
                throw new Exception("Gift multipart contract mismatch.");
            return ++Calls == 1 ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("retry") }
                : new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true},\"eventId\":\"gift\",\"appliedSnapshotId\":\"removed\",\"nextLineageToken\":\"next\"}") };
        }
    }

    // Explicit unsafe fixture for a loopback RimTest extension, run on the game thread.
    // It creates cargo and invokes the real patched engine arrival; it does not test launch UI.
    public static object StartInGame()
    {
        var mod = LoadedModManager.GetMod<ClashOfRimMod>();
        var type = typeof(ClashOfRimMod);
        var markers = (IEnumerable<ModWorldMapMarkerDto>)type.GetProperty("WorldMapMarkersSnapshot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(mod)!;
        var target = markers.First(m => m.OwnerUserId == "rimtest-B" && !string.IsNullOrEmpty(m.MapId));
        var action = new RemoteGiftTransportersArrivalAction();
        string transporterKey = "rimtest-pod-" + Guid.NewGuid().ToString("N");
        void SetAction(string name, object? value) => typeof(RemoteGiftTransportersArrivalAction).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(action, value);
        SetAction("markerId", target.MarkerId); SetAction("targetUserId", target.OwnerUserId); SetAction("targetColonyId", target.OwnerColonyId);
        SetAction("targetSnapshotId", target.SnapshotId); SetAction("targetMapId", target.MapId); SetAction("targetWorldObjectId", target.WorldObjectId);
        SetAction("targetLabel", target.Label); SetAction("transporterKey", transporterKey);
        var voyage = (TravellingTransporters)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.TravellingTransporters);
        voyage.Tile = Find.CurrentMap.Tile; voyage.destinationTile = new PlanetTile(target.Tile, target.TileLayerId); voyage.arrivalAction = action;
        voyage.SetFaction(Faction.OfPlayer); Find.WorldObjects.Add(voyage);
        var cargo = new ActiveTransporterInfo(); var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog); wood.stackCount = 5;
        cargo.innerContainer.TryAdd(wood); voyage.AddTransporter(cargo, false);
        var arrived = typeof(TravellingTransporters).GetMethod("Arrived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var arrivedFlag = typeof(TravellingTransporters).GetField("arrived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var transporters = typeof(TravellingTransporters).GetField("transporters", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        foreach (string name in new[] { "snapshotUploadInProgress", "manualSyncInProgress", "localAtomicMutationPending" })
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            object? previous = field.GetValue(mod);
            if ((bool)previous!) throw new Exception("Fixture requires an idle client.");
            try
            {
                field.SetValue(mod, true); arrived.Invoke(voyage, null);
                if ((bool)arrivedFlag.GetValue(voyage)! || !Find.WorldObjects.AllWorldObjects.Contains(voyage) || !cargo.innerContainer.Contains(wood))
                    throw new Exception("Busy arrival lost cargo or consumed engine arrival flag: " + name);
            }
            finally { field.SetValue(mod, previous); }
        }
        string beforeSnapshot = mod.CurrentSnapshotId;
        string worldObjectId = voyage.GetUniqueLoadID(), thingId = wood.ThingID;
        arrived.Invoke(voyage, null);
        if (!(bool)type.GetField("localAtomicMutationPending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mod)!
            || Find.WorldObjects.AllWorldObjects.Contains(voyage) || ((System.Collections.IList)transporters.GetValue(voyage)!).Count != 0)
            throw new Exception("Arrival did not guard the mutation before engine cleanup.");
        return new { key = $"gift-transport-pods:{mod.UserId}:{beforeSnapshot}:{transporterKey}", beforeSnapshot, worldObjectId, thingId, userId = mod.UserId, colonyId = mod.ColonyId };
    }
}
