using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Support;
using AIRsLight.ClashOfRim.Network.Modules.Transfers;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class SupportClosureTestData
{
    public const string PawnKey = "owner:owner/colony:home/pawn:Pawn_1";
    public static readonly DateTimeOffset OriginalTime = DateTimeOffset.UnixEpoch.AddDays(2);
    public static IReadOnlyList<string> Kinds { get; } = Array.AsReadOnly(new[] { "return", "loss", "reject" });

    public static AuthoritativeEvent Source() => AuthoritativeEventFactory.Create(ServerEventType.SupportPawn,
        new("owner", "home"), new("userA", "colonyA"), "source", true,
        new SupportPawnEventPayload(PawnKey, "original", "Ally", true, null, PawnPackage: Package()), OriginalTime.AddDays(-1))
        with { EventId = "source", Status = ServerEventStatus.DeliveredToClient, RejectionPolicy = EventRejectionPolicy.RejectableByTarget };

    public static PawnExchangePackage Package(string key = PawnKey)
    {
        const string xml = "<saveable Class=\"Pawn\"><id>Pawn_1</id><kindDef>Colonist</kindDef></saveable>";
        return new(1, new(key, "original", "Ally", false, "Player"), new("Human", "Colonist", "Player", "Male"),
            new("Ally", null, null, null, null, null, null), new(false, null, null, null, null), [], [], [],
            new(xml, SafePawnExchangeSerializer.ComputeScribeXmlSha256(xml), []));
    }

    public static AuthoritativeEvent Closure(AuthoritativeEvent source, string kind, string reason = "original")
    {
        var payload = (SupportPawnEventPayload)source.Payload;
        return kind switch
        {
            "reject" => SupportReturns.CreateRejectedSupportPawnReturnEvent(source, payload, reason, false, OriginalTime),
            "loss" => SupportReturns.CreateSupportPawnLossReturnEvent(source, payload, "Ally", reason, false, OriginalTime),
            _ => SupportReturns.CreateSupportPawnFinishReturnEvent(source, payload, Request("return", reason), Package(), false, OriginalTime)
        };
    }

    public static AuthoritativeEvent AsLegacy(AuthoritativeEvent item)
    {
        var payload = (SupportPawnEventPayload)item.Payload;
        if (item.IdempotencyKey.EndsWith(":rejected-return", StringComparison.Ordinal)) return item;
        string key = $"support-{(payload.PawnPackage is null ? "loss" : "finish")}:{payload.SourceEventId}:{payload.ReturnReason}";
        return item with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(item.Type, key), IdempotencyKey = key };
    }

    public static FinishSupportPawnRequest Request(string kind, string reason = "original", string user = "userA",
        string pawnKey = PawnKey, string packageKey = PawnKey, string eventId = "source", string? token = null) =>
        new("request-" + reason, eventId, user, "colonyA", "current", reason, pawnKey, "Ally", kind == "loss",
            kind == "loss" ? null : ItemTransferMapper.ToPawnExchangePackageDto(Package(packageKey)), token);
}
