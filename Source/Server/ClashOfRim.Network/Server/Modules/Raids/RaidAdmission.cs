using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using static AIRsLight.ClashOfRim.Network.Modules.Events.EventProtocolMapper;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidAdmission(DiplomacyRelationRegistry relations, OnlinePresenceRegistry presence,
    RaidProtection protection, Func<ClashOfRimServerConfiguration> configuration)
{
    internal RaidEligibilityRequest Describe(ProtocolIdentity attacker, ProtocolIdentity defender,
        SnapshotIdentity identity, SaveSnapshotIndex index, string targetMapId, DateTimeOffset nowUtc, bool defenderOnline = false) =>
        new(ToEventParty(attacker), ToEventParty(defender),
            relations.GetRelationKind(attacker.UserId, attacker.ColonyId, defender.UserId, defender.ColonyId)
                == DiplomacyRelationRegistry.RelationHostile,
            defenderOnline || presence.IsUserOnline(defender.UserId), nowUtc,
            protection.CurrentUntil(defender.UserId, defender.ColonyId, nowUtc), SnapshotWealth.Total(index) ?? 0,
            identity, index.Maps, targetMapId);

    internal ProtocolResponse? Check(RaidEligibilityRequest request)
    {
        var result = RaidEligibilityChecker.Check(request, RaidPolicies.BuildEligibility(configuration()));
        return result.Eligible ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
            ServerText.Format("Raid.PrepareRejected", ("REASONS", string.Join(", ", result.FailureReasons))));
    }

    internal static RaidOpponentKind OpponentKind(string? kind) =>
        Enum.TryParse(kind, ignoreCase: true, out RaidOpponentKind parsed) ? parsed : RaidOpponentKind.Player;
}
