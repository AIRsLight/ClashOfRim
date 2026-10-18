using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

// Trade constraints describe compatible sources; pawn identity and restoration state are not constraints.
public sealed class BiotechSourceConstraint
{
    public BiotechSourceConstraint(string? role, string? raceDefName,
        IReadOnlyList<string> endogeneDefNames, IReadOnlyList<string> xenogeneDefNames)
    {
        Role = role;
        RaceDefName = raceDefName;
        EndogeneDefNames = endogeneDefNames;
        XenogeneDefNames = xenogeneDefNames;
    }

    public string? Role { get; }
    public string? RaceDefName { get; }
    public IReadOnlyList<string> EndogeneDefNames { get; }
    public IReadOnlyList<string> XenogeneDefNames { get; }

    internal bool HasConstraints => !string.IsNullOrWhiteSpace(Role)
        || !string.IsNullOrWhiteSpace(RaceDefName)
        || EndogeneDefNames.Count > 0
        || XenogeneDefNames.Count > 0;
}
