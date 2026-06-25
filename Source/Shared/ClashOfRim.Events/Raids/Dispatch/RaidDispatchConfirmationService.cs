using System.Security.Cryptography;
using System.Text;

namespace AIRsLight.ClashOfRim.Events;

public static class RaidDispatchConfirmationService
{
    public static RaidDispatchConfirmationResult Confirm(
        RaidDispatchConfirmationRequest request,
        RaidEligibilityPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Eligibility);

        RaidAvailabilitySummary availability = RaidAvailabilityProjector.Project(
            new RaidAvailabilityProjectionRequest(request.Eligibility, policy));

        if (!availability.CanRaid)
        {
            return new RaidDispatchConfirmationResult(availability, Token: null);
        }

        RaidDispatchConfirmationToken token = BuildToken(request);
        return new RaidDispatchConfirmationResult(availability, token);
    }

    private static RaidDispatchConfirmationToken BuildToken(RaidDispatchConfirmationRequest request)
    {
        EventParty attacker = request.Eligibility.Attacker
            ?? throw new InvalidOperationException("Eligible dispatch confirmation requires attacker.");
        EventParty defender = request.Eligibility.Defender
            ?? throw new InvalidOperationException("Eligible dispatch confirmation requires defender.");

        string defenderSnapshotId = request.Eligibility.DefenderSnapshot?.SnapshotId
            ?? throw new InvalidOperationException("Eligible dispatch confirmation requires defender snapshot.");
        string seed = string.Join("|", new[]
        {
            attacker.UserId,
            defender.UserId,
            defender.ColonyId ?? "",
            request.Eligibility.TargetMapUniqueId ?? "",
            defenderSnapshotId,
            request.RequestedAtUtc.ToUnixTimeMilliseconds().ToString()
        });

        return new RaidDispatchConfirmationToken(
            "raid-dispatch:" + StableShortHash(seed),
            attacker.UserId,
            defender.UserId,
            defender.ColonyId,
            request.Eligibility.TargetMapUniqueId,
            defenderSnapshotId,
            request.RequestedAtUtc,
            request.RequestedAtUtc + request.TokenLifetime);
    }

    private static string StableShortHash(string seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }
}
