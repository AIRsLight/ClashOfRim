namespace AIRsLight.ClashOfRim.Events;

public static class RaidEligibilityChecker
{
    public static RaidEligibilityResult Check(
        RaidEligibilityRequest? request,
        RaidEligibilityPolicy? policy = null)
    {
        if (request == null)
        {
            return new RaidEligibilityResult(new[] { RaidEligibilityFailureReason.MissingRequest });
        }

        RaidEligibilityPolicy activePolicy = policy ?? new RaidEligibilityPolicy();
        var reasons = new List<RaidEligibilityFailureReason>();

        if (MissingParty(request.Attacker))
        {
            reasons.Add(RaidEligibilityFailureReason.MissingAttacker);
        }

        if (MissingParty(request.Defender))
        {
            reasons.Add(RaidEligibilityFailureReason.MissingDefender);
        }

        if (!MissingParty(request.Attacker) &&
            !MissingParty(request.Defender) &&
            string.Equals(request.Attacker!.UserId, request.Defender!.UserId, StringComparison.Ordinal))
        {
            reasons.Add(RaidEligibilityFailureReason.AttackerIsDefender);
        }

        if (activePolicy.RequireHostileRelation && !request.IsHostile)
        {
            reasons.Add(RaidEligibilityFailureReason.NotHostile);
        }

        if (activePolicy.RequireDefenderOffline && request.DefenderOnline)
        {
            reasons.Add(RaidEligibilityFailureReason.DefenderOnline);
        }

        if (request.DefenderRaidCooldownUntilUtc.HasValue &&
            request.DefenderRaidCooldownUntilUtc.Value > request.CheckedAtUtc)
        {
            reasons.Add(RaidEligibilityFailureReason.CooldownActive);
        }

        if (request.DefenderWealth < activePolicy.MinimumDefenderWealth)
        {
            reasons.Add(RaidEligibilityFailureReason.DefenderWealthBelowMinimum);
        }

        if (MissingSnapshot(request.DefenderSnapshot))
        {
            reasons.Add(RaidEligibilityFailureReason.MissingDefenderSnapshot);
        }

        if (string.IsNullOrWhiteSpace(request.TargetMapUniqueId))
        {
            reasons.Add(RaidEligibilityFailureReason.MissingTargetMap);
        }
        else if (request.DefenderMaps == null ||
            !request.DefenderMaps.Any(map => MapIdsMatch(map.UniqueId, request.TargetMapUniqueId)))
        {
            reasons.Add(RaidEligibilityFailureReason.TargetMapUnavailable);
        }

        return new RaidEligibilityResult(
            reasons,
            request.DefenderRaidCooldownUntilUtc);
    }

    private static bool MissingParty(EventParty? party)
    {
        return party == null || string.IsNullOrWhiteSpace(party.UserId);
    }

    private static bool MissingSnapshot(AIRsLight.ClashOfRim.Save.SnapshotIdentity? snapshot)
    {
        return snapshot == null ||
            string.IsNullOrWhiteSpace(snapshot.OwnerId) ||
            string.IsNullOrWhiteSpace(snapshot.ColonyId) ||
            string.IsNullOrWhiteSpace(snapshot.SnapshotId);
    }

    private static bool MapIdsMatch(string? left, string? right)
    {
        string? normalizedLeft = NormalizeMapId(left);
        string? normalizedRight = NormalizeMapId(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static string? NormalizeMapId(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return null;
        }

        string trimmed = mapId.Trim();
        return trimmed.StartsWith("Map_", StringComparison.Ordinal)
            ? trimmed["Map_".Length..]
            : trimmed;
    }
}
