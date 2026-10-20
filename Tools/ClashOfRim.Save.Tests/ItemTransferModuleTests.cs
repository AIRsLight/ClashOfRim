using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Transfers;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;

internal static class ItemTransferModuleTests
{
    internal static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                RoundTripAndStore();
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    private static void RoundTripAndStore()
    {
        var reference = new CrossMapPawnReference("pawn-é", "base", "名字", false, "Player",
            new Dictionary<string, string?> { ["origin"] = "İ" });
        var equipment = new PawnExchangeEquipmentItem("weapon", "Gun", "名字", 1, "Good", 55, false,
            true, reference.GlobalId, true, "独特", ["trait"]);
        var pawn = new PawnExchangePackage(SafePawnExchangeSerializer.CurrentPackageVersion, reference,
            new("Human", "Colonist", "Player", "Female"), new("名字", "Thin", "Head", "Hair", null, "#ffffff", "#000000"),
            new(false, 120000, 130000, null, "health"), [equipment], [equipment],
            [new("other", "他", false, "Sibling", "TransferAlien", "TransferAlienColonist"),
                new("pet", "宠物", false, "Bond", "LabradorRetriever", "LabradorRetriever")],
            new("<pawn><def>Human</def></pawn>", null, [new("Pawn_2", "other-placeholder", reference)]),
            [new("provider", "kind", new() { ["key"] = "value" }, "{\"v\":1}")]);
        byte[] xml = Encoding.UTF8.GetBytes("<thing><def>Gun</def><comps><state>复杂</state></comps></thing>");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(xml);
        var thing = new ThingStatePackage(1, "gun", "Gun", "复杂", 1,
            new(Convert.ToBase64String(compressed.ToArray()), null, xml.Length));
        var entries = new[]
        {
            new EventThingReference("pawn-é", "Human", 1, "paid", UniqueWeaponTraits: [], PawnPackage: pawn,
                Metadata: new() { ["custom"] = "中文" }),
            new EventThingReference("gun", "Gun", 1, "paid", "Good", 55, "Inner", "Steel", "Normal", 33,
                true, true, "名字", "pawn-é", "复杂", 45.25f, true, ["trait"], StuffDefName: "Steel", MaxHitPoints: 100,
                MinifiedInnerMaxHitPoints: 80, Metadata: new() { ["custom"] = "İ" }, ThingPackage: thing)
        };
        var dtos = entries.Select(ToThingReferenceDto).ToArray();
        for (int i = 0; i < entries.Length; i++)
        {
            Require(JsonSerializer.Serialize(ToEventThingReference(dtos[i], "paid")) == JsonSerializer.Serialize(entries[i]),
                "Transfer codec lost nested payload or display fields.");
            Require(!ReferenceEquals(entries[i].Metadata, dtos[i].Metadata), "Transfer metadata aliases its source dictionary.");
        }
        var pawns = new PawnPackageRegistry();
        var things = new ThingPackageRegistry();
        var packages = new ItemTransferPackages(pawns, things);
        Require(packages.TryStore(new("userA", "colonyA", "paid"), "operation", dtos, out var stored, out var failure),
            "Transfer storage failed: " + failure?.Message);
        Require(packages.TryStore(new("userA", "colonyA", "paid"), "operation", dtos, out var repeated, out _)
            && stored[0].PawnPackageId == repeated[0].PawnPackageId && stored[1].ThingPackageId == repeated[1].ThingPackageId,
            "Transfer storage duplicated package IDs on retry.");
        Require(stored[0].PawnPackage is null && stored[1].ThingPackage is null
            && stored[0].Metadata!["clashofrim.pawn.thingDef"] == "Human"
            && stored[0].Metadata["clashofrim.pawn.biologicalAgeTicks"] == "120000",
            "Stored transfer did not replace inline packages or preserve summary metadata.");
        Require(pawns.TryGetPackage(stored[0].PawnPackageId!, out var reloadedPawn, out _)
            && JsonSerializer.Serialize(pawn) == JsonSerializer.Serialize(reloadedPawn), "Stored pawn lost complex state.");
        Require(things.TryGetPackage(stored[1].ThingPackageId!, out var reloadedThing, out _)
            && SafeThingStatePackageSerializer.DecompressXml(reloadedThing!.Scribe) == Encoding.UTF8.GetString(xml),
            "Stored thing lost scribe state.");
        var invalid = new ThingReferenceDto("bad", "Gun", 1,
            thingPackage: new ThingStatePackageDto(1, "bad", "Gun", "bad", 1, new("invalid", null, 10), null));
        Require(!packages.TryStore(new("userA", "colonyA", "paid"), "bad", [invalid], out var rejected, out failure)
            && rejected.Count == 0 && failure?.ErrorCode == ProtocolErrorCode.ValidationFailed,
            "Invalid complex payload reached storage.");
        PackageRequests(packages, dtos[0].PawnPackage!, dtos[1].ThingPackage!);
        PreviewRequests(dtos[0].PawnPackage!);
    }

    private static void PackageRequests(ItemTransferPackages packages, PawnExchangePackageDto pawn, ThingStatePackageDto thing)
    {
        var owner = new ProtocolIdentity("userA", "colonyA", "paid");
        var pawnRequest = new StorePawnPackageRequest("pawn-request", owner, pawn);
        var storedPawn = packages.StorePawn(pawnRequest);
        Require(storedPawn.Result.Accepted && storedPawn.PawnGlobalId == pawn.Reference.GlobalId
            && packages.StorePawn(pawnRequest).PawnPackageId == storedPawn.PawnPackageId, "Pawn request lost identity or idempotency.");
        var pawnRead = packages.ReadPawn(new(owner, storedPawn.PawnPackageId!));
        Require(pawnRead.Result.Accepted && JsonSerializer.Serialize(pawnRead.PawnPackage) == JsonSerializer.Serialize(pawn),
            "Pawn request lost nested state on retrieval.");
        var thingRequest = new StoreThingPackageRequest("thing-request", owner, thing);
        var storedThing = packages.StoreThing(thingRequest);
        Require(storedThing.Result.Accepted && !string.IsNullOrWhiteSpace(storedThing.Fingerprint)
            && packages.StoreThing(thingRequest).ThingPackageId == storedThing.ThingPackageId, "Thing request lost fingerprint or idempotency.");
        var thingRead = packages.ReadThing(new(owner, storedThing.ThingPackageId!));
        Require(thingRead.Result.Accepted && JsonSerializer.Serialize(thingRead.ThingPackage!.Scribe) == JsonSerializer.Serialize(thing.Scribe),
            "Thing request changed compressed state.");
        Require(packages.ReadPawn(new(owner, "missing")).Result.ErrorCode == ProtocolErrorCode.EventNotFound
            && packages.ReadThing(new(owner, "missing")).Result.ErrorCode == ProtocolErrorCode.EventNotFound,
            "Unknown package did not retain its not-found result.");
        var missingPawn = packages.StorePawn(new("", owner, pawn));
        var missingThing = packages.StoreThing(new("", owner, thing));
        Require(!missingPawn.Result.Accepted && missingPawn.PawnPackageId is null && missingPawn.PawnGlobalId is null
            && !missingThing.Result.Accepted && missingThing.ThingPackageId is null && missingThing.Fingerprint is null,
            "Invalid package request returned success fields.");
        Require(packages.ReadPawn(new(null!, "missing")).Result.ErrorCode == ProtocolErrorCode.ValidationFailed
            && packages.ReadThing(new(null!, "missing")).Result.ErrorCode == ProtocolErrorCode.ValidationFailed,
            "Missing requester reached a package lookup.");
        Require(packages.StorePawn(new("bad-pawn", owner, new(0, null!, null!, null!, null!, [], [], []))).Result.ErrorCode == ProtocolErrorCode.ValidationFailed
            && packages.StoreThing(new("bad-thing", owner, new(0, "", "Gun", null, 0, new("", null, 0)))).Result.ErrorCode == ProtocolErrorCode.ValidationFailed,
            "Invalid package version reached storage.");
    }

    private static void PreviewRequests(PawnExchangePackageDto pawn)
    {
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg==");
        string directory = Path.Combine(Path.GetTempPath(), "cor-preview-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "server.sqlite");
        try
        {
            var state = new ClashOfRimNetworkState(pawnPackages: new PawnPackageRegistry(new SqlitePawnPackageStore(database), null));
            var owner = new ProtocolIdentity("userA", "colonyA", "paid");
            string id = state.TransferPackages.StorePawn(new("preview-pawn", owner, pawn)).PawnPackageId!;
            var upload = new StorePawnPackagePreviewRequest(owner, id);
            var context = PreviewContext(state, "userA", "colonyA");
            var principal = MultipartSnapshotAuthentication.Principal(context.Request);
            var stored = state.PawnPreviews.Store(upload, png, principal);
            Require(stored.Result.Accepted && stored.ContentSha256?.Length == 64
                && state.PawnPreviews.Store(upload, png, principal).ContentSha256 == stored.ContentSha256,
                "Valid preview failed or changed on retry.");
            Require(state.PawnPreviews.Store(upload, png, null).Result.ErrorCode == ProtocolErrorCode.Unauthorized
                && state.PawnPreviews.Store(new(new("other", "elsewhere", null), id), png, principal).Result.ErrorCode == ProtocolErrorCode.Unauthorized,
                "Preview upload accepted a missing or mismatched principal.");
            var other = PreviewContext(state, "other", "elsewhere");
            Require(state.PawnPreviews.Store(new(new("other", "elsewhere", null), id), png,
                MultipartSnapshotAuthentication.Principal(other.Request)).Result.ErrorCode == ProtocolErrorCode.Unauthorized,
                "Authenticated non-owner replaced another player's preview.");
            Require(state.PawnPreviews.Store(new(owner, "missing"), png, principal).Result.ErrorCode == ProtocolErrorCode.EventNotFound,
                "Missing pawn package accepted a preview.");
            byte[] corrupt = png.ToArray(); corrupt[corrupt.Length / 2] ^= 0x7f;
            foreach (var bytes in new[] { corrupt, new byte[PawnPackagePreviewValidator.MaximumBytes + 1] })
                Require(state.PawnPreviews.Store(upload, bytes, principal).Result.ErrorCode == ProtocolErrorCode.ValidationFailed,
                    "Invalid PNG reached preview storage.");
            var reopened = new PawnPackagePreviews(new PawnPackageRegistry(new SqlitePawnPackageStore(database), null));
            var download = new GetPawnPackagePreviewRequest(owner, id);
            Require(PackageEndpoints.ReadPreview(context.Request, download, reopened) is FileContentHttpResult file
                && file.ContentType == "image/png" && !file.EnableRangeProcessing && file.FileContents.Span.SequenceEqual(png),
                "Preview reopen or binary response changed its bytes or media contract.");
            Require(PackageEndpoints.ReadPreview(other.Request, new(new("other", "elsewhere", null), id), reopened) is FileContentHttpResult,
                "Authenticated recipient could not read a transferred pawn preview.");
            Require(PackageEndpoints.ReadPreview(new DefaultHttpContext().Request, download, reopened) is IStatusCodeHttpResult { StatusCode: 401 }
                && PackageEndpoints.ReadPreview(context.Request, new(null!, id), reopened) is IStatusCodeHttpResult { StatusCode: 400 }
                && PackageEndpoints.ReadPreview(context.Request, new(owner, "missing"), reopened) is IStatusCodeHttpResult { StatusCode: 404 },
                "Preview rejection HTTP contracts changed.");
            var empty = PackageEndpoints.StorePreview(new DefaultHttpContext().Request, state.PawnPreviews).GetAwaiter().GetResult();
            Require(((StorePawnPackagePreviewResponse)((IValueHttpResult)empty).Value!).Result.ErrorCode == ProtocolErrorCode.ValidationFailed,
                "Missing multipart preview did not preserve validation precedence.");
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(upload)), "request");
            form.Add(new ByteArrayContent(png), "payload", "preview.png");
            using var body = new MemoryStream(form.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            context.Request.Body = body; context.Request.ContentType = form.Headers.ContentType!.ToString();
            var result = PackageEndpoints.StorePreview(context.Request, state.PawnPreviews).GetAwaiter().GetResult();
            Require(((StorePawnPackagePreviewResponse)((IValueHttpResult)result).Value!).ContentSha256 == stored.ContentSha256,
                "Multipart preview did not pass the admitted principal and original bytes to storage.");
            var memory = new ClashOfRimNetworkState();
            string memoryId = memory.TransferPackages.StorePawn(new("memory", owner, pawn)).PawnPackageId!;
            Require(memory.PawnPreviews.Store(new(owner, memoryId), png, principal).Result.ErrorCode == ProtocolErrorCode.ServerRejected,
                "Unavailable preview persistence returned success.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

    private static DefaultHttpContext PreviewContext(ClashOfRimNetworkState state, string user, string colony)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[MultipartSnapshotAuthentication.HeaderName] = TradeCommandAuthTests.Token(state, user, colony);
        Require(MultipartSnapshotAuthentication.TryAuthorize(context.Request, state, DateTimeOffset.UtcNow), "Could not authenticate preview fixture.");
        return context;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
