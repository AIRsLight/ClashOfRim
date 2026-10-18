using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Authorization;

// Session ownership is refreshed for every decision, including revalidation after recovery.
internal sealed class ColonyAccessAuthorization(
    SessionPrincipalAuthorization principals,
    DiplomacyRelationRegistry diplomacy,
    RaidPreparationRegistry raidPreparations,
    IAuthoritativeEventLedger ledger,
    Func<bool> pvpEnabled)
{
    internal ProtocolResponse? Authorize(
        string? authToken,
        string userId,
        string colonyId,
        string? authorizationEventId,
        string? authorizationScope,
        DateTimeOffset nowUtc,
        ProtocolErrorCode denialCode = ProtocolErrorCode.ServerRejected)
    {
        if (principals.Authorize(authToken, nowUtc, out var resolved, denialCode) is { } failure) return failure;
        var principal = resolved!;

        if (SessionPrincipalAuthorization.Matches(principal, userId, colonyId))
        {
            return null;
        }

        string scope = authorizationScope ?? string.Empty;
        if (string.Equals(scope, "Scout", StringComparison.OrdinalIgnoreCase)
            && IsAuthorizedScoutTarget(principal, userId, colonyId))
        {
            return null;
        }

        if (string.Equals(scope, "FriendlyObservation", StringComparison.OrdinalIgnoreCase)
            && IsAuthorizedFriendlyObservationTarget(principal, userId, colonyId))
        {
            return null;
        }

        if (string.Equals(scope, "RaidTarget", StringComparison.OrdinalIgnoreCase)
            && IsAuthorizedRaidTargetDownload(principal, userId, colonyId))
        {
            return null;
        }

        if (string.Equals(scope, "RaidPreparation", StringComparison.OrdinalIgnoreCase)
            && raidPreparations.TryAuthorizeDownload(
                authorizationEventId,
                principal.UserId,
                principal.ColonyId,
                userId,
                colonyId,
                nowUtc))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(authorizationEventId)
            && IsAuthorizedByRaidEvent(
                principal,
                userId,
                colonyId,
                authorizationEventId!,
                scope))
        {
            return null;
        }

        return ProtocolResponse.Reject(denialCode, ServerLocalization.Text("Auth.IdentityDenied"));
    }

    private bool IsAuthorizedByRaidEvent(
        AuthTokenPrincipal principal,
        string targetUserId,
        string targetColonyId,
        string authorizationEventId,
        string authorizationScope)
    {
        AuthoritativeEvent? ledgerEvent = ledger.Find(authorizationEventId);
        if (ledgerEvent is null
            || ledgerEvent.Type != ServerEventType.Raid
            || ledgerEvent.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player }
            || (!EventVisibility.MatchesParty(ledgerEvent.Target, targetUserId, targetColonyId)
                && !EventVisibility.MatchesParty(ledgerEvent.Actor, targetUserId, targetColonyId)))
        {
            return false;
        }

        if (EventVisibility.MatchesParty(ledgerEvent.Actor, principal.UserId, principal.ColonyId))
        {
            return true;
        }

        if (!string.Equals(authorizationScope, "RaidObservation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return diplomacy.AreAllied(principal.UserId, principal.ColonyId, ledgerEvent.Actor.UserId, ledgerEvent.Actor.ColonyId)
            || diplomacy.AreAllied(principal.UserId, principal.ColonyId, ledgerEvent.Target.UserId, ledgerEvent.Target.ColonyId);
    }

    private bool IsAuthorizedScoutTarget(
        AuthTokenPrincipal principal,
        string targetUserId,
        string targetColonyId)
    {
        if (string.Equals(principal.UserId, targetUserId, StringComparison.Ordinal)
            && string.Equals(principal.ColonyId, targetColonyId, StringComparison.Ordinal))
        {
            return false;
        }

        return !diplomacy.AreAllied(principal.UserId, principal.ColonyId, targetUserId, targetColonyId);
    }

    private bool IsAuthorizedFriendlyObservationTarget(
        AuthTokenPrincipal principal,
        string targetUserId,
        string targetColonyId)
    {
        if (string.Equals(principal.UserId, targetUserId, StringComparison.Ordinal)
            && string.Equals(principal.ColonyId, targetColonyId, StringComparison.Ordinal))
        {
            return false;
        }

        return diplomacy.AreAllied(principal.UserId, principal.ColonyId, targetUserId, targetColonyId);
    }

    private bool IsAuthorizedRaidTargetDownload(
        AuthTokenPrincipal principal,
        string targetUserId,
        string targetColonyId)
    {
        if (!pvpEnabled())
        {
            return false;
        }

        if (string.Equals(principal.UserId, targetUserId, StringComparison.Ordinal)
            && string.Equals(principal.ColonyId, targetColonyId, StringComparison.Ordinal))
        {
            return false;
        }

        if (diplomacy.AreAllied(principal.UserId, principal.ColonyId, targetUserId, targetColonyId))
        {
            return false;
        }

        return !RaidUnsettledProjector.BuildForAttacker(principal.UserId, principal.ColonyId,
            ledger.ListByTypeForActor(ServerEventType.Raid, principal.UserId, principal.ColonyId), excludedIdempotencyKey: null).Any();
    }
}
