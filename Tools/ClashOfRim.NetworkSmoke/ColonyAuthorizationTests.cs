using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Protocol;

internal static class ColonyAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    internal static void Run()
    {
        SessionOwnership();
        DiplomaticScopes();
        RaidEventGrants();
        RaidPreparationGrants();
        Require(EventVisibility.MatchesParty(new("a"), "a", "any"), "Historical account-wide visibility was lost.");
        Require(!EventVisibility.MatchesParty(new("a", "home"), "A", "home"), "User identity became case insensitive.");
        Require(!EventVisibility.MatchesParty(new("a", "home"), "a", "HOME"), "Colony identity became case insensitive.");
        Console.WriteLine("PASS: independent colony authorization (sessions, diplomacy, PvP, event and preparation grants)");
    }

    private static void SessionOwnership()
    {
        var f = new Fixture();
        string token = f.Login("a", "home");
        Require(f.Allows(token, "a", "home"), "Owner was denied.");
        Require(!f.Allows(token, "a", "other") && !f.Allows(token, "b", "home"), "Ownership crossed identities.");
        Require(!f.Allows(null, "a", "home") && !f.Allows("invalid", "a", "home"), "Unauthenticated request was allowed.");
        string replacement = f.Login("a", "home");
        Require(!f.Allows(token, "a", "home") && f.Allows(replacement, "a", "home"), "Replacement did not invalidate old session.");
        f.Sessions.EndUser("a");
        Require(!f.Allows(replacement, "a", "home"), "Logged-out token still authorized.");
        token = f.Login("a", "home");
        f.Tokens.RevokeForColony("a", "home");
        Require(!f.Allows(token, "a", "home"), "Revoked token still authorized.");
        token = f.Login("a", "home");
        Require(!f.Allows(token, "a", "home", now: Now.AddMinutes(3)), "Expired session still authorized.");
        Require(!f.Allows(token, "a", "home", now: Now.AddHours(13)), "Expired token still authorized.");
    }

    private static void DiplomaticScopes()
    {
        var f = new Fixture();
        string token = f.Login("a", "home");
        Require(f.Allows(token, "b", "target", "sCoUt"), "Neutral target should be scoutable.");
        Require(!f.Allows(token, "b", "target", "FriendlyObservation"), "Neutral target got friendly observation.");
        Require(f.Allows(token, "b", "target", "RaidTarget"), "Eligible raid target was denied.");
        f.PvpEnabled = false;
        Require(!f.Allows(token, "b", "target", "RaidTarget"), "PvP configuration was captured instead of read live.");
        Require(f.Allows(token, "b", "target", "Scout"), "PvP toggle incorrectly disabled scouting.");
        f.PvpEnabled = true;
        f.Ally("a", "home", "b", "target");
        Require(!f.Allows(token, "b", "target", "Scout"), "Ally used hostile scouting scope.");
        Require(f.Allows(token, "b", "target", "FriendlyObservation"), "Ally observation was denied.");
        Require(!f.Allows(token, "b", "target", "RaidTarget"), "Ally became a raid target.");
        f.Raid("a", "home", "c", "third");
        Require(!f.Allows(token, "d", "fourth", "RaidTarget"), "Unsettled attacker raid did not block a new target download.");
        string other = f.Login("e", "fifth");
        Require(f.Allows(other, "d", "fourth", "RaidTarget"), "Another actor's unsettled raid blocked this actor.");
    }

    private static void RaidEventGrants()
    {
        var f = new Fixture();
        string actor = f.Login("a", "home");
        string observer = f.Login("o", "observer");
        var raid = f.Raid("a", "home", "b", "target");
        Require(f.Allows(actor, "b", "target", eventId: raid.EventId), "Actor event grant was denied.");
        Require(!f.Allows(actor, "b", "other", eventId: raid.EventId), "Event grant crossed colonies.");
        Require(!f.Allows(actor, "c", "third", eventId: raid.EventId), "Event grant crossed parties.");
        Require(!f.Allows(actor, "b", "target", eventId: "missing"), "Missing event granted access.");
        Require(!f.Allows(observer, "b", "target", "RaidObservation", raid.EventId), "Unrelated observer got access.");
        f.Ally("o", "observer", "a", "home");
        Require(f.Allows(observer, "b", "target", "RaidObservation", raid.EventId), "Actor ally observation was denied.");
        Require(!f.Allows(observer, "b", "target", eventId: raid.EventId), "Ally bypassed required observation scope.");
        string defenderAlly = f.Login("d", "ally");
        f.Ally("d", "ally", "b", "target");
        Require(f.Allows(defenderAlly, "a", "home", "RaidObservation", raid.EventId), "Defender ally observation was denied.");
        var npc = f.Raid("a", "home", "npc", "npc-site", RaidOpponentKind.VanillaNpc);
        Require(!f.Allows(actor, "npc", "npc-site", eventId: npc.EventId), "NPC raid granted player snapshot access.");
        var unrelated = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("a", "home"), new("b", "target"),
            "unrelated-event", true, raid.Payload, Now);
        f.Ledger.Append(unrelated);
        Require(!f.Allows(actor, "b", "target", eventId: unrelated.EventId), "Non-raid event granted access.");
    }

    private static void RaidPreparationGrants()
    {
        var f = new Fixture();
        string actor = f.Login("a", "home");
        string other = f.Login("o", "other");
        var preparation = f.Preparations.Create("prepare", "raid", "a", "home", "b", "target",
            "snapshot", "world-object", "map", 1, Now, TimeSpan.FromSeconds(20));
        Require(f.Allows(actor, "b", "target", "RaidPreparation", preparation.PreparationId), "Preparation download was denied.");
        Require(!f.Allows(other, "b", "target", "RaidPreparation", preparation.PreparationId), "Preparation crossed principals.");
        Require(!f.Allows(actor, "b", "other", "RaidPreparation", preparation.PreparationId), "Preparation crossed target colonies.");
        Require(!f.Allows(actor, "b", "target", eventId: preparation.PreparationId), "Preparation bypassed required scope.");
        Require(!f.Allows(actor, "b", "target", "RaidPreparation", preparation.PreparationId, Now.AddSeconds(21)), "Expired preparation authorized.");
    }

    private sealed class Fixture
    {
        internal readonly AuthTokenRegistry Tokens = new();
        internal readonly LoginSessionRegistry Sessions = new();
        internal readonly DiplomacyRelationRegistry Diplomacy = new();
        internal readonly RaidPreparationRegistry Preparations = new();
        internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
        internal bool PvpEnabled = true;
        private readonly ColonyAccessAuthorization authorization;

        internal Fixture() => authorization = new(Tokens, Sessions, Diplomacy, Preparations, Ledger, () => PvpEnabled);

        internal string Login(string user, string colony)
        {
            Require(Sessions.TryCreate(user, colony, Now, out string session), "Cannot create fixture session.");
            return Tokens.Issue(user, user, colony, session, Now);
        }

        internal bool Allows(string? token, string user, string colony, string? scope = null, string? eventId = null, DateTimeOffset? now = null)
            => authorization.TryAuthorize(token, user, colony, eventId, scope, now ?? Now, out _);

        internal void Ally(string userA, string colonyA, string userB, string colonyB)
            => Diplomacy.SetRelationKind(userA, colonyA, userB, colonyB, DiplomacyRelationRegistry.RelationAlly, null, Now);

        internal AuthoritativeEvent Raid(string actor, string home, string target, string colony, RaidOpponentKind kind = RaidOpponentKind.Player)
        {
            var raid = AuthoritativeEventFactory.Create(ServerEventType.Raid, new(actor, home), new(target, colony),
                "raid-" + target, true, new RaidEventPayload("snapshot", null, Now, null, null,
                    new RaidAttackForceRecord("attacker-snapshot", [], []), OpponentKind: kind), Now);
            Ledger.Append(raid);
            return raid;
        }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
