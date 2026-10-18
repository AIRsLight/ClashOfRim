using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using System;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Pawns;

internal sealed class BiotechPawnExchangeCompatibility
{
    private readonly Func<bool> enabled;
    private readonly Action<string> warning;

    internal BiotechPawnExchangeCompatibility(Func<bool> enabled, Action<string> warning)
    {
        this.enabled = enabled;
        this.warning = warning;
    }

    internal void Append(Pawn pawn, ModPawnExchangePackageDto package)
    {
        if (!enabled() || pawn?.genes is null || package is null) return;
        var extension = BiotechPawnExchangePayload.Create(pawn.genes.Xenotype?.defName, BiotechSavedXenotypePackages.BuildPackage(pawn));
        if (extension is not null) package.Extensions.Add(extension);
    }

    internal bool TryRegister(ModPawnExchangePackageDto package, out string message)
    {
        message = string.Empty;
        if (!enabled()) return true;
        var payload = BiotechPawnExchangePayload.ReadCustomXenotype(package, warning);
        return payload is null || BiotechSavedXenotypePackages.TryRegister(payload, out message);
    }

    internal void ResetRestoredPawnGeneCaches(Pawn pawn)
    {
        if (!enabled() || pawn?.genes is null || Find.UniqueIDsManager is null) return;
        BiotechGeneRuntime.ResetCustomXenotypeCaches(pawn);
    }
}
