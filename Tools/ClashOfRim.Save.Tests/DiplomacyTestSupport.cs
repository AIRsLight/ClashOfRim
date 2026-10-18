using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class DiplomacyTestSupport
{
    internal static EconomicModuleTestContext Fixture(Func<string, SaveSnapshotPackage> factory,
        IAuthoritativeEventLedger? ledger = null, DiplomacyRelationRegistry? relations = null,
        Action<string, EventNotificationHub>? diplomacySignal = null)
    {
        var f = new EconomicModuleTestContext(factory, ledger: ledger, relations: relations, diplomacySignal: diplomacySignal)
            { Configuration = new(diplomacyRelationChangeCooldown: TimeSpan.Zero, diplomacySupportRequestCooldown: TimeSpan.Zero) };
        var identity = new SnapshotIdentity("userB", "colonyB", "target");
        f.Snapshots.StoreLatest(new(identity, f.Package.Envelope with { Identity = identity }, f.Package.Index, DateTimeOffset.UtcNow));
        return f;
    }
    internal static CreateDiplomacyEventRequest Request(EconomicModuleTestContext f, string kind, string key = "request", string? token = null)
        => new(key, new("userA", "colonyA", "before"), new("userB", "colonyB", "target"), kind, "盟约-é-İ", DateTimeOffset.UnixEpoch.AddDays(100), token ?? f.Token);
    internal static RespondDiplomacyEventRequest Response(string id, string token, bool accepted = true)
        => new(id, "userB", "colonyB", "target", accepted, "decision-é-İ", token);
    internal static string Initial(string kind) => kind switch
    { "AllianceCancellation" or "SupportRequest" => "Ally", "PeaceRequest" => "Hostile", _ => "Neutral" };
    internal static string Final(string kind) => kind switch
    { "AllianceRequest" or "SupportRequest" => "Ally", "WarDeclaration" => "Hostile", _ => "Neutral" };
    internal static void SetRelation(EconomicModuleTestContext f, string relation, DateTimeOffset? now = null)
        => f.Relations.SetRelationKind("userA", "colonyA", "userB", "colonyB", relation, "initial", now ?? DateTimeOffset.UtcNow.AddDays(-10));
    internal static string Relation(EconomicModuleTestContext f) => f.Relations.GetRelationKind("userA", "colonyA", "userB", "colonyB");
    internal static string Evidence(EconomicModuleTestContext f) => JsonSerializer.Serialize(f.Ledger.ListAll()) + JsonSerializer.Serialize(f.Relations.List());

}
