using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;

internal readonly struct ReproductiveSourceRecord
{
    public ReproductiveSourceRecord(
        string? Role,
        string? GlobalId,
        string? OwnerUserId,
        string? Name,
        string? RaceDefName,
        string? PawnKindDefName,
        string? Gender,
        bool Dead,
        string? FactionDefName,
        string? XenotypeDefName,
        IReadOnlyList<string>? EndogeneDefNames,
        IReadOnlyList<string>? XenogeneDefNames)
    {
        this.Role = Role;
        this.GlobalId = GlobalId;
        this.OwnerUserId = OwnerUserId;
        this.Name = Name;
        this.RaceDefName = RaceDefName;
        this.PawnKindDefName = PawnKindDefName;
        this.Gender = Gender;
        this.Dead = Dead;
        this.FactionDefName = FactionDefName;
        this.XenotypeDefName = XenotypeDefName;
        this.EndogeneDefNames = EndogeneDefNames ?? Array.Empty<string>();
        this.XenogeneDefNames = XenogeneDefNames ?? Array.Empty<string>();
    }

    public string? Role { get; }

    public string? GlobalId { get; }

    public string? OwnerUserId { get; }

    public string? Name { get; }

    public string? RaceDefName { get; }

    public string? PawnKindDefName { get; }

    public string? Gender { get; }

    public bool Dead { get; }

    public string? FactionDefName { get; }

    public string? XenotypeDefName { get; }

    public IReadOnlyList<string> EndogeneDefNames { get; }

    public IReadOnlyList<string> XenogeneDefNames { get; }
}
