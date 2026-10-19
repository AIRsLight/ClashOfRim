using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fixture = EconomicEndpointFixture;
using Reply = EconomicEndpointFixture.Reply;
using static SnapshotTestAssertions;

internal static class EconomicRequestBindingTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        VerifyCanonicalBinding();
        foreach (string kind in new[] { "gift", "order", "self-delivery", "drop-pod" })
        {
            foreach (bool reopen in new[] { false, true }) Fixture.With(factory, kind, f => VerifyBoundReplay(f, reopen));
            foreach (bool pending in new[] { false, true }) Fixture.With(factory, kind, f => VerifyUnboundHistory(f, pending));
            foreach (string fault in new[] { "binding", "payload" }) Fixture.With(factory, kind, f => VerifyJournalConflict(f, fault));
            foreach (bool bytes in new[] { false, true }) Fixture.With(factory, kind, f => VerifyBindingRace(f, bytes));
            Fixture.With(factory, kind, VerifyMalformedBinding);
        }
    }

    private static void VerifyCanonicalBinding()
    {
        var a = JsonSerializer.Deserialize<JsonElement>("{\"b\":2,\"a\":[{\"z\":0,\"x\":1},2]}");
        var b = JsonSerializer.Deserialize<JsonElement>("{\"a\":[{\"x\":1,\"z\":0},2],\"b\":2}");
        string hash = SnapshotRequestBinding.Hash(a, [1, 2, 3]);
        Check(hash == "v1:BD4633A7AB9D6AF5D84EE5FAD4687FBE47B62124900BC7D02888BA18D69BC35A" && hash == SnapshotRequestBinding.Hash(b, [1, 2, 3]) && hash != SnapshotRequestBinding.Hash(a, [1, 2, 4])
            && SnapshotRequestBinding.Canonical(a) == "{\"a\":[{\"x\":1,\"z\":0},2],\"b\":2}", "Canonical request binding lost values or property-order independence.");
        var changed = JsonSerializer.Deserialize<JsonElement>("{\"b\":2,\"a\":[2,{\"z\":0,\"x\":1}]}");
        Check(hash != SnapshotRequestBinding.Hash(changed, [1, 2, 3]), "Binding ignored array order.");
    }

    private static void VerifyBoundReplay(Fixture f, bool reopen)
    {
        f.Store.Reset();
        var created = f.Call(); Check(created.Result.Accepted, "Binding fixture did not commit.");
        if (reopen) f.Open();
        string before = f.Evidence(); f.Store.Reset();
        foreach (var (name, change) in BindingMutations(f.Kind))
        {
            var rejected = f.Call(mutate: change);
            Check(!rejected.Result.Accepted && rejected.Id is null && rejected.Snapshot is null && rejected.Token is null
                && rejected.Owner is null && rejected.Buyer is null && before == f.Evidence() && f.Store.Writes == 0,
                "Bound replay accepted or changed " + f.Kind + "/" + name + "/" + reopen);
        }
        var corrupted = f.Package.Payload.ToArray(); corrupted[^1] ^= 1;
        var badBytes = f.Call(bytes: corrupted);
        Check(!badBytes.Result.Accepted && badBytes.Token is null && before == f.Evidence() && f.Store.Writes == 0,
            "Replay trusted metadata instead of actual snapshot bytes: " + f.Kind);
        var replay = f.Call(mutate: ReverseProperties);
        SameReply(created, replay);
        Check(before == f.Evidence() && f.Store.Writes == 0, "Identical retry changed persisted state.");
        // Later delivery/order states must stay advanced while the original request remains replayable.
        var main = f.State.Ledger.Find(created.Id!)!;
        if (f.Kind is "gift" or "order") f.State.Ledger.ChangeStatus(main.EventId, ServerEventStatus.Cancelled);
        else foreach (var delivery in f.State.Ledger.ListByType(ServerEventType.ItemDelivery))
            f.State.Ledger.ChangeStatus(delivery.EventId, ServerEventStatus.AppliedToSnapshot);
        before = f.Evidence(); SameReply(created, f.Call());
        Check(before == f.Evidence(), "Replay reopened a terminal event.");
        var newer = f.Package with { Envelope = f.Package.Envelope with { Identity = new("userA", "colonyA", "newer"), NextLineageToken = "newer-secret" } };
        f.Store.Inner.StoreLatest(newer, newer.Index, DateTimeOffset.UtcNow);
        before = f.Evidence(); var historical = f.Call();
        Check(historical.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && historical.Id == created.Id
            && historical.Snapshot == created.Snapshot && historical.Token is null && before == f.Evidence(),
            "Historical replay disclosed a newer snapshot credential.");
        f.AssertReleased();
    }

    private static void SameReply(Reply original, Reply replay) => Check(replay.Result.Accepted
        && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.Id == original.Id
        && replay.Snapshot == original.Snapshot && replay.Token == original.Token && replay.Owner == original.Owner
        && replay.Buyer == original.Buyer && !replay.Created, "Replay lost its original confirmation.");

    private static IEnumerable<(string, Action<JsonObject>)> BindingMutations(string kind)
    {
        string operation = kind == "gift" ? "Gift" : kind == "order" ? "TradeOrder" : "Fulfillment";
        string actor = kind == "gift" ? "Actor" : kind == "order" ? "Owner" : "Acceptor";
        string items = kind == "gift" ? "Things" : kind == "order" ? "RequestedThings" : "DeliveredThings";
        JsonObject Item(JsonObject root) => root[operation]![items]![0]!.AsObject();
        yield return ("parent", root => root[operation]![actor]!["SnapshotId"] = "other-parent");
        foreach (string field in new[] { "PackageVersion", "OwnerId", "ColonyId", "SnapshotId", "RimWorldVersion", "PayloadEncoding",
            "OriginalSha256", "PayloadSha256", "PreviousSnapshotId", "LineageToken", "NextLineageToken", "SnapshotUploadKind" })
            yield return ("metadata-" + field, root => root["ConfirmedSnapshot"]![field] = "changed");
        foreach (string field in new[] { "OriginalSaveBytes", "PayloadBytes", "GameTicks", "DefenderThreatPoints" })
            yield return ("metadata-" + field, root => root["ConfirmedSnapshot"]![field] = 987654);
        foreach (string field in new[] { "GlobalKey", "DefName", "Quality", "StuffDefName", "DisplayLabel", "PawnPackageId", "ThingPackageId" })
            yield return ("item-" + field, root => Item(root)[field] = "changed");
        yield return ("count", root => Item(root)["StackCount"] = 999);
        yield return ("nested-metadata", root => Item(root)["Metadata"] = new JsonObject { ["nested"] = "changed" });
        yield return ("traits", root => Item(root)["UniqueWeaponTraits"] = new JsonArray("changed"));
        yield return ("extra-item", root => root[operation]![items]!.AsArray().Add(Item(root).DeepClone()));
        if (kind == "gift")
        {
            yield return ("message", root => root[operation]!["Message"] = "changed");
            yield return ("delivery-kind", root => root[operation]!["DeliveryKind"] = "Forced");
            yield return ("target-snapshot", root => root[operation]!["Target"]!["SnapshotId"] = "changed");
            yield return ("target-context", root => root[operation]!["TargetContext"] = new JsonObject { ["MapUniqueId"] = "changed" });
        }
        else if (kind == "order")
        {
            yield return ("fee", root => root[operation]!["FeeSilver"] = 101);
            yield return ("self-pickup", root => root[operation]!["AllowSelfPickup"] = false);
            yield return ("drop-pod", root => root[operation]!["AllowServerDropPod"] = false);
            yield return ("offer", root => root[operation]!["OfferedThings"] = new JsonArray(Item(root).DeepClone()));
        }
        else
        {
            yield return ("order", root => root[operation]!["TradeEventId"] = "changed");
            yield return ("memo", root => root[operation]!["AcceptedMemoEventId"] = "changed");
            yield return ("mode", root => root[operation]!["FulfillmentMode"] = kind == "drop-pod" ? "SelfDelivery" : "ServerDropPod");
        }
    }

    private static void ReverseProperties(JsonObject root)
    {
        foreach (var value in root.ToArray())
        {
            if (value.Value is JsonObject child) ReverseProperties(child);
            else if (value.Value is JsonArray array) foreach (var item in array.OfType<JsonObject>()) ReverseProperties(item);
        }
        var properties = root.ToArray(); root.Clear();
        foreach (var property in properties.Reverse()) root.Add(property.Key, property.Value);
    }

    private static void VerifyUnboundHistory(Fixture f, bool pending)
    {
        string id;
        if (pending)
        {
            var intent = f.Seed(); id = (intent.Gift ?? intent.Trade!.Batch.Events[0]).EventId;
            string path = Directory.GetFiles(f.Journal, "*.json").Single();
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            RemoveHashes(node); File.WriteAllText(path, node.ToJsonString());
        }
        else { id = f.Call().Id!; f.SetMainHash(null); }
        f.Open(); f.Store.Reset();
        var response = f.Call();
        Check(!response.Result.Accepted && response.Id is null && response.Token is null
            && f.State.Ledger.Find(id) is not null && f.MainHash() is null && !f.State.SnapshotCommits.HasPending
            && f.Store.Writes == (pending ? 1 : 0), "Legacy operation was lost or guessed a request binding.");
        f.Open(); f.Store.Reset(); string before = f.Evidence();
        Check(!f.Call().Result.Accepted && before == f.Evidence() && f.Store.Writes == 0 && f.MainHash() is null,
            "Legacy replay silently adopted the first new request.");
        f.AssertReleased();
    }

    private static void RemoveHashes(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("CreationRequestHash");
            foreach (var child in obj.Select(p => p.Value).OfType<JsonNode>()) RemoveHashes(child);
        }
        else if (node is JsonArray array) foreach (var child in array.OfType<JsonNode>()) RemoveHashes(child);
    }

    private static void VerifyMalformedBinding(Fixture f)
    {
        Check(f.Call().Result.Accepted, "Malformed binding fixture did not commit.");
        foreach (string hash in new[] { "", "v2:" + new string('0', 64), "v1:broken" })
        {
            f.SetMainHash(hash); f.Open(); f.Store.Reset(); string before = f.Evidence();
            Check(!f.Call().Result.Accepted && before == f.Evidence() && f.Store.Writes == 0, "Malformed stored binding was accepted.");
        }
    }

    private static void VerifyJournalConflict(Fixture f, string fault)
    {
        var intent = f.Seed(); f.Open(); f.Store.Reset();
        var main = intent.Gift ?? intent.Trade!.Batch.Events[0];
        var bad = fault == "binding" ? Fixture.WithHash(main, "v1:" + new string('0', 64)) : main with
        {
            Payload = main.Payload is ItemDeliveryEventPayload gift ? gift with { Message = "changed" }
                : ((TradeEventPayload)main.Payload) with { FeeSilver = 999 }
        };
        f.State.Ledger.Append(bad); string before = f.Evidence(); bool failed = false;
        try { f.State.SnapshotMutations.Recover(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { failed = true; }
        Check(failed && before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending,
            "Recovery accepted conflicting persisted request content: " + f.Kind + "/" + fault);
        f.AssertReleased();
    }

    private static void VerifyBindingRace(Fixture f, bool bytes)
    {
        f.Store.Reset();
        Action<JsonObject> Change(int i) => root => root[f.Kind == "gift" ? "Gift" : f.Kind == "order" ? "TradeOrder" : "Fulfillment"]!
            [f.Kind == "gift" ? "Things" : f.Kind == "order" ? "RequestedThings" : "DeliveredThings"]![0]!["StackCount"] = 100 + i;
        var replies = SnapshotReplayRace.Run(i => Task.FromResult(bytes ? f.Call(bytes: i == 1 ? null : new byte[] { (byte)i })
            : f.Call(mutate: Change(i))), () => { });
        Check(replies.Count(r => r.Result.Accepted) == 1 && f.Store.Writes == 1, "Changed same-key requests shared a successful confirmation.");
        int winner = Array.FindIndex(replies, r => r.Result.Accepted);
        SameReply(replies[winner], bytes ? f.Call() : f.Call(mutate: Change(winner + 1)));
        Check(replies.Where(r => !r.Result.Accepted).All(r => r.Id is null && r.Token is null && r.Snapshot is null), "Conflicting race leaked a confirmation.");
        f.AssertReleased();
    }

}
